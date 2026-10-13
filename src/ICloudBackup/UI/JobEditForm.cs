using ICloudBackup.Core;

namespace ICloudBackup.UI;

public sealed class JobEditForm : Form
{
    private readonly TextBox _name = new() { Width = 420 };
    private readonly TextBox _source = new() { Width = 420 };
    private readonly TextBox _dest = new() { Width = 420 };
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly CheckBox _keepLocal = new() { Text = "Keep files on this device (download everything from iCloud and keep it local)", AutoSize = true };
    private readonly TextBox _excludes = new() { Width = 420, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical };

    public BackupJob Job { get; }

    public JobEditForm(BackupJob job)
    {
        Job = job;
        Text = string.IsNullOrEmpty(job.Name) ? "Add backup job" : $"Edit backup job - {job.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9F);

        var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(string label, Control c, Control? extra = null)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
            t.Controls.Add(c);
            if (extra != null) t.Controls.Add(extra); else t.Controls.Add(new Label { AutoSize = true });
        }

        var browseSrc = new Button { Text = "Browse...", AutoSize = true };
        browseSrc.Click += (_, _) => Browse(_source, "Choose the iCloud folder to back up");
        var browseDst = new Button { Text = "Browse...", AutoSize = true };
        browseDst.Click += (_, _) => Browse(_dest, "Choose the NAS folder to back up to");

        Row("Name", _name);
        Row("iCloud folder", _source, browseSrc);
        Row("NAS folder", _dest, browseDst);
        t.Controls.Add(new Label { AutoSize = true });
        t.Controls.Add(_enabled);
        t.Controls.Add(new Label { AutoSize = true });
        t.Controls.Add(new Label { AutoSize = true });
        t.Controls.Add(_keepLocal);
        t.Controls.Add(new Label { AutoSize = true });
        Row("Skip files named\n(one per line, * and ? allowed)", _excludes);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        t.Controls.Add(new Label { AutoSize = true });
        t.Controls.Add(buttons);
        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;

        _name.Text = job.Name;
        _source.Text = job.SourcePath;
        _dest.Text = job.DestinationPath;
        _enabled.Checked = job.Enabled;
        _keepLocal.Checked = job.KeepFilesLocal;
        _excludes.Text = string.Join(Environment.NewLine, job.ExcludePatterns);

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_source.Text) || string.IsNullOrWhiteSpace(_dest.Text))
            {
                MessageBox.Show(this, "Name, iCloud folder and NAS folder are all required.", "iCloud Backup");
                DialogResult = DialogResult.None;
                return;
            }
            if (PathsOverlap(_source.Text.Trim(), _dest.Text.Trim()))
            {
                MessageBox.Show(this, "The NAS folder can't be inside the iCloud folder (or the other way round).", "iCloud Backup");
                DialogResult = DialogResult.None;
                return;
            }
            Job.Name = _name.Text.Trim();
            Job.SourcePath = _source.Text.Trim().TrimEnd('\\');
            Job.DestinationPath = _dest.Text.Trim().TrimEnd('\\');
            Job.Enabled = _enabled.Checked;
            Job.KeepFilesLocal = _keepLocal.Checked;
            Job.ExcludePatterns = _excludes.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        };
    }

    private static bool PathsOverlap(string a, string b)
    {
        try
        {
            var fa = Path.GetFullPath(a).TrimEnd('\\') + "\\";
            var fb = Path.GetFullPath(b).TrimEnd('\\') + "\\";
            return fa.StartsWith(fb, StringComparison.OrdinalIgnoreCase) || fb.StartsWith(fa, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void Browse(TextBox target, string description)
    {
        using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }
}
