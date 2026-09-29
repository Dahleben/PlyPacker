using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace PlyPackerGUI
{
    public class PlyData
    {
        private string _name;
        public string Name
        {
            get => string.IsNullOrWhiteSpace(_name) ? "Unnamed" : _name;
            set => _name = value;
        }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Angle { get; set; }
        public bool boolIsolate { get; set; }
    }

    public partial class MainWindow : Window
    {
        private ObservableCollection<PlyData> PlyList { get; set; } = new ObservableCollection<PlyData>();

        // This caches the results of the packing engine in memory!
        private List<EnginePly> _lastPackedPlies = null;

        public MainWindow()
        {
            InitializeComponent();
            PlyDataGrid.ItemsSource = PlyList;
            Console.SetOut(new TextBoxStreamWriter(ConsoleOutput));
            Console.WriteLine("System Ready. Load a CSV, add data manually, or Paste (Ctrl+V) from Excel.");
        }

        private void PlyDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // NEW: If the user is actively editing a cell (cursor is in a TextBox), 
            // ignore our custom logic and let them paste/type normally!
            if (e.OriginalSource is System.Windows.Controls.TextBox)
            {
                return;
            }

            // Handle Ctrl+V for Excel Pasting across multiple cells
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                PasteFromExcel();
                e.Handled = true;
                return;
            }

            // Fix 'Enter' to move focus down instead of completing row edit and stopping
            if (e.Key == Key.Enter)
            {
                PlyDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
                PlyDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

                var direction = Keyboard.Modifiers == ModifierKeys.Shift
                    ? FocusNavigationDirection.Up
                    : FocusNavigationDirection.Down;

                if (e.OriginalSource is UIElement uiElement)
                {
                    uiElement.MoveFocus(new TraversalRequest(direction));
                }

                e.Handled = true;
            }
        }

        private void PasteFromExcel()
        {
            string clipboardText = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(clipboardText)) return;

            string[] rows = clipboardText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length == 0) return;

            int startRow = 0, startCol = 0;

            if (PlyDataGrid.SelectedCells.Count > 0)
            {
                var firstCell = PlyDataGrid.SelectedCells[0];
                startCol = PlyDataGrid.Columns.IndexOf(firstCell.Column);
                startRow = PlyList.IndexOf(firstCell.Item as PlyData);
            }

            if (startRow < 0) startRow = 0;
            if (startCol < 0) startCol = 0;

            for (int i = 0; i < rows.Length; i++)
            {
                string[] cells = rows[i].Split('\t');
                int currentRow = startRow + i;

                if (currentRow >= PlyList.Count) PlyList.Add(new PlyData { Name = "New_Ply", Length = 0, Width = 0, Angle = 0, boolIsolate = false });

                PlyData targetPly = PlyList[currentRow];

                for (int j = 0; j < cells.Length; j++)
                {
                    int currentCol = startCol + j;
                    if (currentCol >= PlyDataGrid.Columns.Count) break;

                    string cellValue = cells[j].Trim();

                    switch (currentCol)
                    {
                        case 0:
                            if (!string.IsNullOrWhiteSpace(cellValue)) targetPly.Name = cellValue;
                            break;
                        case 1:
                            if (double.TryParse(cellValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double l)) targetPly.Length = l;
                            break;
                        case 2:
                            if (double.TryParse(cellValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double w)) targetPly.Width = w;
                            break;
                        case 3:
                            if (double.TryParse(cellValue, NumberStyles.Any, CultureInfo.InvariantCulture, out double a)) targetPly.Angle = a;
                            break;
                        case 4:
                            if (bool.TryParse(cellValue, out bool iso)) targetPly.boolIsolate = iso;
                            else if (cellValue == "1") targetPly.boolIsolate = true;
                            else if (cellValue == "0") targetPly.boolIsolate = false;
                            break;
                    }
                }
            }
            PlyDataGrid.Items.Refresh();
            Console.WriteLine($"Pasted {rows.Length} rows from clipboard.");
        }

        private void BtnLoadCSV_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", Title = "Select TubePly CSV" };
            if (openFileDialog.ShowDialog() == true) LoadCSVData(openFileDialog.FileName);
        }

        private void LoadCSVData(string filePath)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                if (lines.Length == 0) return;

                string headerLine = lines[0].Replace("\uFEFF", "").Replace("\uEFBBBF", "").Replace("\"", "").ToLower();
                var headers = new List<string>(headerLine.Split(',').Select(h => h.Trim()));

                int nameIdx = headers.IndexOf("name"), lenIdx = headers.IndexOf("length"), widIdx = headers.IndexOf("width"), angIdx = headers.IndexOf("angle"), isoIdx = headers.IndexOf("isolate");

                if (nameIdx == -1 || lenIdx == -1 || widIdx == -1 || angIdx == -1)
                {
                    MessageBox.Show("CSV missing required headers: name, length, width, angle", "Import Error");
                    return;
                }

                PlyList.Clear();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var row = lines[i].Replace("\"", "").Split(',');
                    if (row.Length <= Math.Max(angIdx, Math.Max(lenIdx, Math.Max(nameIdx, widIdx)))) continue;

                    if (double.TryParse(row[angIdx], NumberStyles.Any, CultureInfo.InvariantCulture, out double angle))
                    {
                        bool isolateState = false;
                        if (isoIdx != -1 && row.Length > isoIdx) { string rawIso = row[isoIdx].Trim().ToLower(); isolateState = (rawIso == "true" || rawIso == "1"); }
                        PlyList.Add(new PlyData
                        {
                            Name = row[nameIdx].Trim(),
                            Length = double.Parse(row[lenIdx], CultureInfo.InvariantCulture),
                            Width = double.Parse(row[widIdx], CultureInfo.InvariantCulture),
                            Angle = angle,
                            boolIsolate = isolateState
                        });
                    }
                }
                Console.WriteLine($"Successfully loaded {PlyList.Count} plies from CSV.");
            }
            catch (Exception ex) { MessageBox.Show($"Error reading file: {ex.Message}"); }
        }

        private void BtnAddRow_Click(object sender, RoutedEventArgs e) { PlyList.Add(new PlyData { Name = "New_Ply", Length = 10, Width = 10, Angle = 0, boolIsolate = false }); }

        private void BtnDuplicateRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedPlies = PlyDataGrid.SelectedCells.Select(c => c.Item as PlyData).Where(p => p != null).Distinct().ToList();
            if (selectedPlies.Count > 0)
            {
                foreach (var ply in selectedPlies)
                {
                    PlyList.Add(new PlyData { Name = ply.Name + "_copy", Length = ply.Length, Width = ply.Width, Angle = ply.Angle, boolIsolate = ply.boolIsolate });
                    Console.WriteLine($"Duplicated ply: {ply.Name}");
                }
            }
            else MessageBox.Show("Please select at least one cell or row to duplicate.", "Information");
        }

        private void BtnRemoveRow_Click(object sender, RoutedEventArgs e)
        {
            var selectedPlies = PlyDataGrid.SelectedCells.Select(c => c.Item as PlyData).Where(p => p != null).Distinct().ToList();
            if (selectedPlies.Count > 0)
            {
                foreach (var ply in selectedPlies) PlyList.Remove(ply);
                Console.WriteLine($"Removed {selectedPlies.Count} plies.");
            }
            else MessageBox.Show("Please select at least one cell or row to remove.", "Information");
        }
        private void BtnOpenHelp_Click(object sender, RoutedEventArgs e)
        {
            HelpWindow hw = new HelpWindow();
            hw.Show();
        }

        private void BtnPack_Click(object sender, RoutedEventArgs e)
        {
            if (PlyList.Count == 0)
            {
                MessageBox.Show("No data to pack.", "Warning");
                return;
            }

            try
            {
                double len = double.Parse(TxtBoxLength.Text);
                double wid = double.Parse(TxtBoxWidth.Text);
                double gap = double.Parse(TxtBoxGap.Text);
                double step = double.Parse(TxtBoxStep.Text);

                double[] dz = new double[4];
                dz[0] = double.Parse(TxtBoxDzTop.Text); dz[1] = double.Parse(TxtBoxDzBottom.Text);
                dz[2] = double.Parse(TxtBoxDzLeft.Text); dz[3] = double.Parse(TxtBoxDzRight.Text);

                Console.WriteLine("-----------------------------------");
                Console.WriteLine("Starting Packing Engine -> GENERATING PREVIEW...");

                var inputList = PlyList.ToList();

                // Gray out the buttons while the engine runs
                BtnPack.IsEnabled = false;
                BtnSave.IsEnabled = false;

                System.Threading.Tasks.Task.Run(() =>
                {
                    var packedPlies = PackerEngine.PackPlies(inputList, len, wid, gap, step, dz);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _lastPackedPlies = packedPlies; // Cache in memory

                        // Re-enable the Pack button. Only enable Save button if plies were successfully packed!
                        BtnPack.IsEnabled = true;
                        BtnSave.IsEnabled = (_lastPackedPlies != null && _lastPackedPlies.Count > 0);

                        if (packedPlies.Count > 0)
                        {
                            PreviewWindow pw = new PreviewWindow(packedPlies, len, wid);
                            pw.Show();
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid input parameters. Ensure all inputs are valid numbers.\n{ex.Message}", "Input Error");
                BtnPack.IsEnabled = true; // Ensure button re-enables if there was a typo in the inputs
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPackedPlies == null || _lastPackedPlies.Count == 0)
            {
                MessageBox.Show("Please pack the plies first by clicking 'PACK PLIES & PREVIEW'.", "Information");
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog { Filter = "AutoCAD Script (*.scr)|*.scr", Title = "Save AutoCAD Script As", FileName = "PlyOutput.scr" };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    double len = double.Parse(TxtBoxLength.Text);
                    double wid = double.Parse(TxtBoxWidth.Text);
                    double gap = double.Parse(TxtBoxGap.Text);
                    double txtSize = double.Parse(TxtBoxTxtSize.Text);
                    bool rotate = BoolTxtRt.IsChecked == true;
                    bool keepBB = BoolKeepBB.IsChecked == true;

                    // Gray out the buttons while the file is being written to disk
                    BtnPack.IsEnabled = false;
                    BtnSave.IsEnabled = false;

                    System.Threading.Tasks.Task.Run(() =>
                    {
                        PackerEngine.WriteScript(_lastPackedPlies, len, wid, keepBB, gap, txtSize, rotate, saveFileDialog.FileName);

                        // Re-enable the buttons once the file write is fully complete
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            BtnPack.IsEnabled = true;
                            BtnSave.IsEnabled = true;
                        });
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Invalid input parameters.\n{ex.Message}", "Input Error");
                    BtnPack.IsEnabled = true;
                    BtnSave.IsEnabled = true;
                }
            }
        }
    }

    public class TextBoxStreamWriter : TextWriter
    {
        private System.Windows.Controls.TextBox _output;
        public TextBoxStreamWriter(System.Windows.Controls.TextBox output) { _output = output; }
        public override void Write(char value) { Application.Current.Dispatcher.Invoke(() => { _output.AppendText(value.ToString()); _output.ScrollToEnd(); }); }
        public override void Write(string value) { Application.Current.Dispatcher.Invoke(() => { _output.AppendText(value); _output.ScrollToEnd(); }); }
        public override Encoding Encoding => Encoding.UTF8;
    }
}