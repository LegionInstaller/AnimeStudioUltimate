using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AnimeStudio.Migoto;

namespace AnimeStudio.GUI
{
    /// <summary>
    /// Picks a 3DMigoto mod folder and decides which loaded renderer each of its parts takes
    /// the place of. Built in code rather than through the designer -- the layout is a folder
    /// row, a grid and three buttons, and a .Designer.cs would only obscure that.
    ///
    /// The mod cannot say which renderer it belongs to: its ini names the game's buffers by
    /// hashes taken while the game ran, and those exist nowhere in the shipped assets. What it
    /// does give away is how many bones a part needs, and that alone usually leaves one
    /// candidate. The suggestion is offered; the choice stays with the user.
    /// </summary>
    public partial class MigotoForm : Form
    {
        private readonly TextBox folderBox = new TextBox();
        private readonly Button browse = new Button();
        private readonly DataGridView grid = new DataGridView();
        private readonly DataGridView variables = new DataGridView();
        private readonly Label summary = new Label();
        private readonly Button apply = new Button();
        private readonly Button clear = new Button();
        private readonly Button close = new Button();

        private MigotoMod mod;
        private List<(string Name, int Bones)> renderers = new List<(string, int)>();
        private readonly List<string> warnings = new List<string>();

        private const string NoTarget = "(do not replace)";

        public MigotoForm()
        {
            Text = "Replace meshes from a 3DMigoto mod";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 460);
            Size = new Size(900, 560);

            var folderLabel = new Label { Text = "Mod folder", AutoSize = true, Left = 12, Top = 15 };
            folderBox.SetBounds(90, 12, 640, 23);
            folderBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            folderBox.ReadOnly = true;
            browse.SetBounds(740, 11, 100, 25);
            browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browse.Text = "Browse...";
            browse.Click += (s, e) => Browse();

            grid.SetBounds(12, 48, 828, 300);
            grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "part", HeaderText = "Mod part", ReadOnly = true, FillWeight = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "verts", HeaderText = "Vertices", ReadOnly = true, FillWeight = 45 });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            { Name = "needs", HeaderText = "Needs bones", ReadOnly = true, FillWeight = 50 });
            grid.Columns.Add(new DataGridViewComboBoxColumn
            { Name = "target", HeaderText = "Replaces", FillWeight = 150, FlatStyle = FlatStyle.Flat });

            var switchLabel = new Label
            { Text = "Variants", AutoSize = true, Left = 12, Top = 356 };
            switchLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            variables.SetBounds(12, 374, 300, 80);
            variables.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            variables.AllowUserToAddRows = false;
            variables.AllowUserToDeleteRows = false;
            variables.RowHeadersVisible = false;
            variables.ColumnHeadersVisible = false;
            variables.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            variables.Columns.Add(new DataGridViewTextBoxColumn { ReadOnly = true, FillWeight = 60 });
            variables.Columns.Add(new DataGridViewComboBoxColumn { FillWeight = 40, FlatStyle = FlatStyle.Flat });

            summary.SetBounds(324, 374, 516, 80);
            summary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            apply.SetBounds(560, 466, 90, 28);
            clear.SetBounds(656, 466, 90, 28);
            close.SetBounds(752, 466, 88, 28);
            foreach (var b in new[] { apply, clear, close })
                b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            apply.Text = "Apply";
            clear.Text = "Clear";
            close.Text = "Close";
            apply.Click += (s, e) => Apply();
            clear.Click += (s, e) => { MigotoSwap.Disarm(); Describe(); };
            close.Click += (s, e) => Close();

            Controls.AddRange(new Control[]
            {
                folderLabel, folderBox, browse, grid, switchLabel, variables, summary,
                apply, clear, close,
            });
            Describe();
        }

        private void Browse()
        {
            var dialog = new OpenFolderDialog { Title = "Choose the mod folder" };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            LoadFolder(dialog.Folder);
        }

        private void LoadFolder(string folder)
        {
            var inis = Directory.GetFiles(folder, "*.ini");
            if (inis.Length == 0)
            {
                MessageBox.Show(this, "No .ini in that folder.", "Nothing to read",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            warnings.Clear();
            try
            {
                mod = MigotoMod.Load(inis[0]);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "The ini could not be read",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mod = null;
                return;
            }
            warnings.AddRange(mod.Warnings);
            folderBox.Text = folder;
            renderers = MigotoSwap.LoadedRenderers();
            Fill();
            Describe();
        }

        private void Fill()
        {
            grid.Rows.Clear();
            var targets = new List<string> { NoTarget };
            targets.AddRange(renderers.Select(r => $"{r.Name}  ({r.Bones} bones)"));

            foreach (var part in mod.Parts)
            {
                var row = new DataGridViewRow();
                row.CreateCells(grid);
                var cell = (DataGridViewComboBoxCell)row.Cells[3];
                cell.Items.Add(NoTarget);
                cell.Value = NoTarget;
                row.Cells[0].Value = part.Name + (part.IsDrawn ? "" : "  (not drawn)");
                row.Cells[1].Value = part.VertexCount > 0 ? (object)part.VertexCount : "?";

                // One unreadable part must not take the whole dialog down with it. Folders are
                // hand-edited and mods are written by several tool versions, so a part that
                // cannot be made sense of is shown as such and the rest stays usable.
                try
                {
                    var (highest, _) = part.BoneRange(warnings);
                    row.Cells[2].Value = highest >= 0 ? (object)(highest + 1) : "?";

                    var fits = MigotoSwap.Candidates(
                        part, Path.GetFileNameWithoutExtension(mod.IniPath), renderers, warnings);
                    // Only what could actually work is offered, so a wrong pick is hard to make.
                    foreach (var fit in fits)
                        cell.Items.Add($"{fit.Name}  ({fit.Bones} bones)");
                    // Only preselect when the name actually points somewhere. With none of the
                    // right character's renderers loaded, every candidate is a stranger that
                    // merely has enough bones, and guessing one of those is how a mod ends up
                    // on the wrong body.
                    if (part.IsDrawn && highest >= 0 && fits.Count > 0 && fits[0].Shared > 0)
                        cell.Value = $"{fits[0].Name}  ({fits[0].Bones} bones)";
                }
                catch (Exception ex)
                {
                    warnings.Add($"{part.Name}: could not be read, {ex.Message}");
                    row.Cells[2].Value = "?";
                }
                // An unreadable part keeps its row but stays on "(do not replace)", and Apply
                // only looks at rows that name a target.
                row.Tag = part;
                grid.Rows.Add(row);
            }

            // One row per variable the mod switches with. A cycle variable such as
            // "$body = 0,1,2" becomes a choice, an on/off one a 0/1 choice.
            variables.Rows.Clear();
            foreach (var kv in mod.Variables.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                if (kv.Key == "$active")
                    continue;                       // the mod's own on switch, always on
                var row = new DataGridViewRow();
                row.CreateCells(variables);
                row.Cells[0].Value = kv.Key;
                var choice = (DataGridViewComboBoxCell)row.Cells[1];
                foreach (var value in kv.Value)
                    choice.Items.Add(value);
                choice.Value = mod.Defaults.TryGetValue(kv.Key, out var d) && kv.Value.Contains(d)
                    ? d : kv.Value.FirstOrDefault();
                variables.Rows.Add(row);
            }
        }

        private void Apply()
        {
            if (mod == null)
            {
                MessageBox.Show(this, "Choose a mod folder first.", "Nothing loaded",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var mapping = new Dictionary<string, MigotoPart>(StringComparer.Ordinal);
            foreach (DataGridViewRow row in grid.Rows)
            {
                var target = row.Cells[3].Value as string;
                if (target == null || target == NoTarget || row.Tag is not MigotoPart part)
                    continue;
                var name = target.Substring(0, target.LastIndexOf("  (", StringComparison.Ordinal));
                if (mapping.ContainsKey(name))
                {
                    MessageBox.Show(this, $"{name} is assigned twice. One renderer can only "
                                          + "take one part.", "Ambiguous",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                mapping[name] = part;
            }

            if (mapping.Count == 0)
            {
                MigotoSwap.Disarm();
                Describe();
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["$active"] = "1",
            };
            foreach (DataGridViewRow row in variables.Rows)
                values[(string)row.Cells[0].Value] = row.Cells[1].Value as string ?? "0";
            MigotoSwap.Arm(mod, mapping, values);
            Logger.Info($"Mesh replacement armed: {mapping.Count} part(s) from "
                        + $"{Path.GetFileName(mod.Folder)}. The next model export uses them.");
            Describe();
        }

        private void Describe()
        {
            var lines = new List<string>();
            if (mod != null && grid.Rows.Cast<DataGridViewRow>()
                    .Any(r => r.Tag is MigotoPart p && p.IsDrawn
                              && (r.Cells[3].Value as string) == NoTarget))
                lines.Add("Some parts have no suggestion -- load the character they belong to, "
                          + "or pick a target yourself.");
            lines.Add(MigotoSwap.IsArmed
                ? $"Armed: {Path.GetFileName(MigotoSwap.Folder)}. The next model export "
                  + "replaces the assigned meshes."
                : "Nothing armed -- exports are unaffected.");
            var seen = warnings.Distinct().ToList();
            if (seen.Count > 0)
            {
                lines.Add("");
                lines.AddRange(seen.Take(3));
                if (seen.Count > 3)
                    lines.Add($"... and {seen.Count - 3} more, see the log");
                foreach (var w in seen)
                    Logger.Warning("Mesh replacement: " + w);
            }
            summary.Text = string.Join(Environment.NewLine, lines);
        }
    }
}
