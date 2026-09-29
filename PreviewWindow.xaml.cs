using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlyPackerGUI
{
    public partial class PreviewWindow : Window
    {
        public PreviewWindow(List<EnginePly> plies, double sheetLen, double sheetWid)
        {
            InitializeComponent();
            DrawPreview(plies, sheetLen, sheetWid);
        }

        private void DrawPreview(List<EnginePly> plies, double sheetLen, double sheetWid)
        {
            PreviewCanvas.Children.Clear();
            PreviewCanvas.Width = sheetLen;
            PreviewCanvas.Height = sheetWid;

            // Draw the background border (the stock sheet)
            var sheetRect = new Rectangle
            {
                Width = sheetLen,
                Height = sheetWid,
                Stroke = Brushes.Black,
                StrokeThickness = Math.Max(sheetLen, sheetWid) * 0.003, // Scales line thickness relative to sheet size
                Fill = Brushes.White
            };
            Canvas.SetLeft(sheetRect, 0);
            Canvas.SetTop(sheetRect, 0);
            PreviewCanvas.Children.Add(sheetRect);

            // Draw the individual plies
            foreach (var ply in plies)
            {
                if (ply.FinalPoly == null) continue;

                // 1. Draw the Polygon Geometry
                var polyShape = new Polygon
                {
                    Stroke = Brushes.DarkBlue,
                    StrokeThickness = Math.Max(sheetLen, sheetWid) * 0.0015,
                    Fill = new SolidColorBrush(Color.FromArgb(120, 0, 120, 215)) // Semi-transparent blue fill
                };

                // Convert geometry coordinates to WPF points (inverting Y-axis for AutoCAD compatibility)
                foreach (var coord in ply.FinalPoly.Coordinates)
                {
                    polyShape.Points.Add(new Point(coord.X, sheetWid - coord.Y));
                }
                PreviewCanvas.Children.Add(polyShape);

                // 2. Add the Text Label
                var centroid = ply.FinalPoly.Centroid;
                var textLabel = new TextBlock
                {
                    Text = ply.Name,
                    Foreground = Brushes.Black,
                    FontSize = Math.Max(sheetLen, sheetWid) * 0.012, // Scale text dynamically with sheet size
                    FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false // Prevents the text from blocking mouse clicks if you add hover effects later
                };

                // Force WPF to calculate the physical pixel dimensions of the text string *before* it renders
                textLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                // Position at the centroid, offset by exactly half the text's width and height so it centers perfectly
                Canvas.SetLeft(textLabel, centroid.X - (textLabel.DesiredSize.Width / 2));
                Canvas.SetTop(textLabel, (sheetWid - centroid.Y) - (textLabel.DesiredSize.Height / 2));

                PreviewCanvas.Children.Add(textLabel);
            }
        }
    }
}