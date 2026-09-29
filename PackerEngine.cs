using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Distance;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace PlyPackerGUI
{
    public class EnginePly
    {
        public string Name { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Angle { get; set; }
        public bool Isolate { get; set; }
        public Geometry LocalPoly { get; set; }
        public Geometry FinalPoly { get; set; }
    }

    public class Subgroup
    {
        public List<Geometry> PaddedPolys { get; set; } = new List<Geometry>();
        public List<EnginePly> Plies { get; set; } = new List<EnginePly>();
    }

    public class AngleGroup
    {
        public double Angle { get; set; }
        public Geometry Contour { get; set; }
        public List<EnginePly> Plies { get; set; } = new List<EnginePly>();
    }

    public static class PackerEngine
    {
        static readonly GeometryFactory Gf = NtsGeometryServices.Instance.CreateGeometryFactory();

        // PHASE 1: Performs purely mathematical heavy-lifting packing algorithms
        public static List<EnginePly> PackPlies(IEnumerable<PlyData> inputData, double bboxLen, double bboxWid, double gap, double step, double[] dz)
        {
            try
            {
                double dzTop = dz[0], dzBottom = dz[1], dzLeft = dz[2], dzRight = dz[3];
                double effLen = bboxLen - dzLeft - dzRight;
                double effWid = bboxWid - dzTop - dzBottom;

                if (effLen <= 0 || effWid <= 0)
                {
                    Console.WriteLine("Error: The deadzone is larger than the bounding box itself. No printable area left.");
                    return new List<EnginePly>();
                }

                var pliesByAngle = new Dictionary<double, List<EnginePly>>();
                var isolatedPlies = new List<EnginePly>();

                foreach (var pd in inputData)
                {
                    var newPly = new EnginePly { Name = pd.Name, Length = pd.Length, Width = pd.Width, Angle = pd.Angle, Isolate = pd.boolIsolate };
                    if (newPly.Isolate) isolatedPlies.Add(newPly);
                    else
                    {
                        if (!pliesByAngle.ContainsKey(pd.Angle)) pliesByAngle[pd.Angle] = new List<EnginePly>();
                        pliesByAngle[pd.Angle].Add(newPly);
                    }
                }

                Console.WriteLine("Stage 1: Pre-rotating and packing parallel plies into nested subgroups...");
                var groupsData = new List<AngleGroup>();

                foreach (var kvp in pliesByAngle)
                {
                    groupsData.AddRange(PackPreRotatedSubgroups(kvp.Value, gap, kvp.Key, effLen, effWid, step));
                }

                foreach (var isoPly in isolatedPlies)
                {
                    double l = isoPly.Length, w = isoPly.Width;
                    var coords = new Coordinate[] {
                        new Coordinate(-l/2, -w/2), new Coordinate(l/2, -w/2),
                        new Coordinate(l/2, w/2), new Coordinate(-l/2, w/2), new Coordinate(-l/2, -w/2)
                    };
                    var basePoly = Gf.CreatePolygon(coords);
                    isoPly.LocalPoly = Rotate(basePoly, isoPly.Angle);
                    var bufferParams = new BufferParameters { JoinStyle = JoinStyle.Mitre };
                    groupsData.Add(new AngleGroup
                    {
                        Angle = isoPly.Angle,
                        Plies = new List<EnginePly> { isoPly },
                        Contour = isoPly.LocalPoly.Buffer(gap / 2.0, bufferParams)
                    });
                }

                Console.WriteLine($"Stage 2: Scanning effective bounding box for {groupsData.Count} grouped contours...");
                double packedArea = PackGroupsGlobally(groupsData, effLen, effWid, step);

                var allPlies = groupsData.SelectMany(g => g.Plies).Where(p => p.FinalPoly != null).ToList();

                if (gap == 0 && allPlies.Any())
                {
                    Console.WriteLine("Stage 3: Closing gaps between plies...");
                    double increment = 0.005;

                    for (int passNum = 0; passNum < 4; passNum++)
                    {
                        allPlies = allPlies.OrderBy(p => p.FinalPoly.EnvelopeInternal.MinY).ToList();
                        for (int i = 0; i < allPlies.Count; i++)
                        {
                            var plyDict = allPlies[i];
                            var poly = plyDict.FinalPoly;
                            var otherPolys = allPlies.Where((p, idx) => idx != i).Select(p => p.FinalPoly).ToList();
                            Geometry env = otherPolys.Any() ? UnaryUnionOp.Union(otherPolys) : null;

                            while (true)
                            {
                                var testPoly = Translate(poly, 0, -increment);
                                if (testPoly.EnvelopeInternal.MinY < -1e-5) { poly = Translate(poly, 0, -poly.EnvelopeInternal.MinY); break; }
                                if (env != null && (testPoly.Intersection(env).Area > 1e-6 || poly.Distance(env) <= 0.02)) break;
                                poly = testPoly;
                            }

                            if (Math.Abs(plyDict.Angle) > 1e-4)
                            {
                                double slideIncrement = plyDict.Angle > 0 ? increment : -increment;
                                while (true)
                                {
                                    var testPoly = Translate(poly, slideIncrement, 0);
                                    if (testPoly.EnvelopeInternal.MinX < -1e-5 || testPoly.EnvelopeInternal.MaxX > effLen + 1e-5) break;
                                    if (env != null && (testPoly.Intersection(env).Area > 1e-6 || poly.Distance(env) <= 0.02)) break;
                                    poly = testPoly;
                                }
                            }
                            plyDict.FinalPoly = poly;
                        }
                    }

                    Console.WriteLine("Stage 4: Consolidating ply edges...");
                    for (int passNum = 0; passNum < 4; passNum++)
                    {
                        allPlies = allPlies.OrderByDescending(p => p.Length * p.Width).ToList();
                        for (int i = 0; i < allPlies.Count; i++)
                        {
                            var polyI = allPlies[i].FinalPoly;
                            double areaI = allPlies[i].Length * allPlies[i].Width;
                            double[] bestShift = null;

                            var candidates = new List<Tuple<double, double, int, Geometry>>();
                            for (int j = 0; j < allPlies.Count; j++)
                            {
                                if (i == j) continue;
                                var polyJ = allPlies[j].FinalPoly;
                                double dist = polyI.Distance(polyJ);
                                if (dist > 0 && dist <= 0.6) candidates.Add(new Tuple<double, double, int, Geometry>(dist, allPlies[j].Length * allPlies[j].Width, j, polyJ));
                            }

                            candidates = candidates.OrderByDescending(c => c.Item2).ThenBy(c => c.Item1).ToList();

                            foreach (var cand in candidates)
                            {
                                var nearest = DistanceOp.NearestPoints(polyI, cand.Item4);
                                double shiftX = nearest[1].X - nearest[0].X, shiftY = nearest[1].Y - nearest[0].Y;

                                if (!(cand.Item2 >= areaI * 0.95 || shiftY <= 1e-4)) continue;

                                var testPoly = Translate(polyI, shiftX, shiftY);
                                if (testPoly.EnvelopeInternal.MinY >= -1e-4)
                                {
                                    var envList = allPlies.Where((p, idx) => idx != i).Select(p => p.FinalPoly).ToList();
                                    var env = envList.Any() ? UnaryUnionOp.Union(envList) : null;
                                    if (env == null || testPoly.Intersection(env).Area <= 1e-6) { bestShift = new double[] { shiftX, shiftY }; break; }
                                }
                            }
                            if (bestShift != null) allPlies[i].FinalPoly = Translate(polyI, bestShift[0], bestShift[1]);
                        }
                    }
                    double minY = allPlies.Min(p => p.FinalPoly.EnvelopeInternal.MinY);
                    if (Math.Abs(minY) > 1e-4) foreach (var p in allPlies) p.FinalPoly = Translate(p.FinalPoly, 0, -minY);
                }

                // Stage 5: Deadzone Offset Shift
                if (dzLeft != 0.0 || dzBottom != 0.0) foreach (var p in allPlies) p.FinalPoly = Translate(p.FinalPoly, dzLeft, dzBottom);

                // Metrics
                double maxUsedX = allPlies.Any() ? allPlies.Max(p => p.FinalPoly.EnvelopeInternal.MaxX) : 0;
                double totalLinearLengthNeeded = maxUsedX + dzRight;
                double lengthUtilization = (totalLinearLengthNeeded / bboxLen) * 100;
                double areaEfficiency = (packedArea / (totalLinearLengthNeeded * bboxWid)) * 100;

                Console.WriteLine("-----------------------------------");
                Console.WriteLine("PACKING COMPLETE!");
                Console.WriteLine($"Linear Stock Needed:  {totalLinearLengthNeeded:F2} units (Out of {bboxLen:F2} total)");
                Console.WriteLine($"Length Utilization:   {lengthUtilization:F1}% of the allocated sheet length");
                Console.WriteLine($"True Area Efficiency: {areaEfficiency:F1}% (Material used vs. Material cut from roll)");
                Console.WriteLine("-----------------------------------");

                return allPlies;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CRITICAL ERROR: {ex.Message}\n{ex.StackTrace}");
                return new List<EnginePly>();
            }
        }

        // PHASE 2: Accepts pre-computed geometry from memory and instantly dumps it to disk.
        public static void WriteScript(List<EnginePly> allPlies, double bboxLen, double bboxWid, bool keepBB, double gap, double txtSize, bool rotate, string outputFile)
        {
            try
            {
                Console.WriteLine($"Writing AutoCAD script to: {outputFile}...");
                using (var scr = new StreamWriter(outputFile))
                {
                    scr.WriteLine("OSMODE 0");
                    scr.WriteLine("-LAYER\nMAKE\n0\nCOLOR\n7\n0\n");
                    scr.WriteLine("-LAYER\nMAKE\nCUT\nCOLOR\n1\nCUT\n");
                    scr.WriteLine("-LAYER\nMAKE\nTEXT\nCOLOR\n7\nTEXT\n");

                    if (keepBB)
                    {
                        scr.WriteLine("-LAYER\nSET\n0\n");
                        scr.WriteLine($"RECTANG\n0,0\n{bboxLen},{bboxWid}");
                    }

                    scr.WriteLine("-LAYER\nSET\nCUT\n");

                    if (gap == 0)
                    {
                        var allLines = new List<Geometry>();
                        foreach (var ply in allPlies)
                        {
                            var coords = ply.FinalPoly.Coordinates;
                            for (int v = 0; v < coords.Length - 1; v++)
                            {
                                var p1 = new Coordinate(Math.Round(coords[v].X, 3), Math.Round(coords[v].Y, 3));
                                var p2 = new Coordinate(Math.Round(coords[v + 1].X, 3), Math.Round(coords[v + 1].Y, 3));
                                if (!p1.Equals2D(p2)) allLines.Add(Gf.CreateLineString(new[] { p1, p2 }));
                            }
                        }

                        var mergedLinesUnion = UnaryUnionOp.Union(allLines);
                        var merger = new LineMerger();
                        merger.Add(mergedLinesUnion);
                        var mergedLinesCol = merger.GetMergedLineStrings();

                        var simplifier = new TopologyPreservingSimplifier(Gf.BuildGeometry(mergedLinesCol));
                        simplifier.DistanceTolerance = 0.001;
                        var simplifiedGeom = simplifier.GetResultGeometry();

                        for (int i = 0; i < simplifiedGeom.NumGeometries; i++)
                        {
                            var geom = simplifiedGeom.GetGeometryN(i);
                            if (geom is LineString ls)
                            {
                                scr.WriteLine("PLINE");
                                foreach (var coord in ls.Coordinates) scr.WriteLine($"{coord.X:F4},{coord.Y:F4}");
                                scr.WriteLine("");
                            }
                        }
                    }
                    else
                    {
                        foreach (var ply in allPlies)
                        {
                            scr.WriteLine("PLINE");
                            var coords = ply.FinalPoly.Coordinates;
                            for (int i = 0; i < coords.Length - 1; i++) scr.WriteLine($"{coords[i].X:F4},{coords[i].Y:F4}");
                            scr.WriteLine("C");
                        }
                    }

                    scr.WriteLine("-LAYER\nSET\nTEXT\n");
                    foreach (var ply in allPlies)
                    {
                        var centroid = ply.FinalPoly.Centroid;
                        scr.WriteLine("TEXT\nJ\nMC");
                        scr.WriteLine($"{centroid.X:F4},{centroid.Y:F4}");
                        scr.WriteLine($"{txtSize:F4}");
                        scr.WriteLine(rotate ? $"{ply.Angle}" : "0");
                        scr.WriteLine($"{ply.Name}");
                    }

                    scr.WriteLine("-PURGE A * N");
                }
                Console.WriteLine("Done! Script saved successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing script: {ex.Message}");
            }
        }

        static Geometry Rotate(Geometry geom, double angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180.0;
            var rotation = AffineTransformation.RotationInstance(radians, 0, 0);
            return rotation.Transform(geom);
        }

        static Geometry Translate(Geometry geom, double x, double y)
        {
            var translation = AffineTransformation.TranslationInstance(x, y);
            return translation.Transform(geom);
        }

        static List<AngleGroup> PackPreRotatedSubgroups(List<EnginePly> plies, double gap, double angle, double effLen, double effWid, double step)
        {
            plies = plies.OrderByDescending(p => p.Length * p.Width).ToList();
            var subgroups = new List<Subgroup>();

            foreach (var ply in plies)
            {
                double l = ply.Length, w = ply.Width;
                var coords = new Coordinate[] {
                    new Coordinate(-l/2, -w/2), new Coordinate(l/2, -w/2),
                    new Coordinate(l/2, w/2), new Coordinate(-l/2, w/2), new Coordinate(-l/2, -w/2)
                };
                var basePoly = Gf.CreatePolygon(coords);
                var rotatedBase = Rotate(basePoly, angle);

                var bufferParams = new BufferParameters { JoinStyle = JoinStyle.Mitre };
                var paddedBase = rotatedBase.Buffer(gap / 2.0, bufferParams);

                var env = paddedBase.EnvelopeInternal;
                double pmx = env.MinX, pmy = env.MinY;
                double baseW = env.MaxX - pmx, baseH = env.MaxY - pmy;

                bool placed = false;

                foreach (var subgroup in subgroups)
                {
                    var sgPoly = UnaryUnionOp.Union(subgroup.PaddedPolys);
                    double scanMaxX = Math.Min(effLen - baseW, sgPoly.EnvelopeInternal.MaxX + baseW);
                    double scanMaxY = Math.Min(effWid - baseH, effWid);

                    for (double cx = 0.0; cx <= scanMaxX && !placed; cx += step)
                    {
                        for (double cy = 0.0; cy <= scanMaxY && !placed; cy += step)
                        {
                            double shiftX = cx - pmx, shiftY = cy - pmy;
                            var testPadded = Translate(paddedBase, shiftX, shiftY);

                            if (testPadded.EnvelopeInternal.MaxX <= effLen && testPadded.EnvelopeInternal.MaxY <= effWid)
                            {
                                if (!subgroup.PaddedPolys.Any(pPoly => testPadded.Intersection(pPoly).Area > 1e-6))
                                {
                                    ply.LocalPoly = Translate(rotatedBase, shiftX, shiftY);
                                    subgroup.PaddedPolys.Add(testPadded);
                                    subgroup.Plies.Add(ply);
                                    placed = true;
                                }
                            }
                        }
                    }
                    if (placed) break;
                }

                if (!placed)
                {
                    double shiftX = -pmx, shiftY = -pmy;
                    var testPadded = Translate(paddedBase, shiftX, shiftY);

                    if (testPadded.EnvelopeInternal.MaxX <= effLen && testPadded.EnvelopeInternal.MaxY <= effWid)
                    {
                        ply.LocalPoly = Translate(rotatedBase, shiftX, shiftY);
                        subgroups.Add(new Subgroup { PaddedPolys = new List<Geometry> { testPadded }, Plies = new List<EnginePly> { ply } });
                    }
                    else Console.WriteLine($"WARNING: Ply '{ply.Name}' is too large for the effective bounding box.");
                }
            }

            var finalGroups = new List<AngleGroup>();
            foreach (var sg in subgroups) finalGroups.Add(new AngleGroup { Angle = angle, Plies = sg.Plies, Contour = UnaryUnionOp.Union(sg.PaddedPolys) });
            return finalGroups;
        }

        static double PackGroupsGlobally(List<AngleGroup> groupsData, double effLen, double effWid, double step)
        {
            groupsData = groupsData.OrderByDescending(g => g.Contour.EnvelopeInternal.MaxX - g.Contour.EnvelopeInternal.MinX)
                                   .ThenByDescending(g => g.Contour.Area)
                                   .ToList();

            var mainBbox = Gf.CreatePolygon(new Coordinate[] {
                new Coordinate(0,0), new Coordinate(effLen,0), new Coordinate(effLen,effWid), new Coordinate(0,effWid), new Coordinate(0,0)
            });
            var placedContours = new List<Geometry>();
            double packedArea = 0;

            foreach (var group in groupsData)
            {
                var contour = group.Contour;
                var env = contour.EnvelopeInternal;
                double minx = env.MinX, miny = env.MinY;

                bool placed = false;
                for (double cx = 0.0; cx <= effLen - (env.MaxX - minx) && !placed; cx += step)
                {
                    for (double cy = 0.0; cy <= effWid - (env.MaxY - miny) && !placed; cy += step)
                    {
                        double shiftX = cx - minx, shiftY = cy - miny;
                        var testContour = Translate(contour, shiftX, shiftY);

                        if (mainBbox.Contains(testContour))
                        {
                            if (!placedContours.Any(pc => testContour.Intersection(pc).Area > 1e-6))
                            {
                                placedContours.Add(testContour);
                                foreach (var ply in group.Plies)
                                {
                                    ply.FinalPoly = Translate(ply.LocalPoly, shiftX, shiftY);
                                    packedArea += (ply.Length * ply.Width);
                                }
                                placed = true;
                            }
                        }
                    }
                }
                if (!placed)
                {
                    string groupName = group.Plies.Count == 1 && group.Plies[0].Isolate ? $"Isolated Ply '{group.Plies[0].Name}'" : $"Angle group {group.Angle}";
                    Console.WriteLine($"WARNING: Could not fit {groupName}.");
                }
            }
            return packedArea;
        }
    }
}