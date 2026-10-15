using AdbSync.Core;

namespace AdbSync.Gui;

/// <summary>Selezione di una cartella sul telefono (navigazione via shell adb).</summary>
internal sealed class RemoteFolderDialog : Form
{
    private readonly IRemoteDevice _device;
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private CancellationTokenSource? _cts;

    public string SelectedPath => SyncEngine.NormalizeRemoteRoot(_path.Text);

    private static readonly string[] Shortcuts =
    {
        "/sdcard", "/sdcard/DCIM", "/sdcard/Pictures", "/sdcard/Movies", "/sdcard/Download", "/sdcard/Documents",
        "/sdcard/Music", "/sdcard/Android/media/com.whatsapp/WhatsApp/Media", "/sdcard/WhatsApp/Media", "/storage",
    };

    public RemoteFolderDialog(IRemoteDevice device, string startPath)
    {
        _device = device;
        Text = "Scegli la cartella sul telefono";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(LogicalToDeviceUnits(620), LogicalToDeviceUnits(520));
        MinimumSize = new Size(LogicalToDeviceUnits(420), LogicalToDeviceUnits(360));
        ShowInTaskbar = false;
        MinimizeBox = false;

        var up = new Button { Text = "↑ Su", AutoSize = true };
        var go = new Button { Text = "Vai", AutoSize = true };
        var shortcuts = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        shortcuts.Items.Add("Scorciatoie...");
        shortcuts.Items.AddRange(Shortcuts);
        shortcuts.SelectedIndex = 0;
        var ok = new Button { Text = "Seleziona questa cartella", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annulla", AutoSize = true, DialogResult = DialogResult.Cancel };

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_path, 0, 0);
        top.Controls.Add(go, 1, 0);
        top.Controls.Add(up, 2, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(24)));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(shortcuts, 0, 1);
        root.Controls.Add(_list, 0, 2);
        root.Controls.Add(_status, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);
        CancelButton = cancel; // niente AcceptButton: Invio serve per navigare

        _path.Text = string.IsNullOrWhiteSpace(startPath) ? "/sdcard" : startPath;
        up.Click += (_, _) => Navigate(ParentPath(SelectedPath));
        go.Click += (_, _) => Navigate(SelectedPath);
        _path.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(SelectedPath); } };
        _list.DoubleClick += (_, _) => { if (_list.SelectedItem is string d) Navigate(SelectedPath.TrimEnd('/') + "/" + d); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _list.SelectedItem is string d) { e.SuppressKeyPress = true; Navigate(SelectedPath.TrimEnd('/') + "/" + d); }
            if (e.KeyCode == Keys.Back) Navigate(ParentPath(SelectedPath));
        };
        shortcuts.SelectedIndexChanged += (_, _) => { if (shortcuts.SelectedIndex > 0) Navigate((string)shortcuts.SelectedItem!); };
        Shown += (_, _) => Navigate(SelectedPath);
        FormClosed += (_, _) => _cts?.Cancel();
    }

    private static string ParentPath(string p)
    {
        int i = p.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    private async void Navigate(string path)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        path = SyncEngine.NormalizeRemoteRoot(path);
        _path.Text = path;
        _status.Text = "Lettura...";
        _list.Items.Clear();
        try
        {
            var dirs = await Task.Run(() => _device.ListDirectoriesAsync(path, cts.Token));
            if (cts.IsCancellationRequested) return;
            _list.BeginUpdate();
            foreach (var d in dirs) _list.Items.Add(d);
            _list.EndUpdate();
            _status.Text = dirs.Count == 0 ? "Nessuna sottocartella (o cartella non accessibile)." : $"{dirs.Count} sottocartelle — doppio clic per entrare.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) _status.Text = "Errore: " + ex.Message;
        }
    }
}

/// <summary>Modifica delle esclusioni (una per riga).</summary>
internal sealed class ExcludesDialog : Form
{
    private readonly TextBox _text = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 10),
    };

    public List<string> Patterns => _text.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    public ExcludesDialog(IEnumerable<string> patterns)
    {
        Text = "Esclusioni";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(480));
        ShowInTaskbar = false;
        MinimizeBox = false;

        var help = new Label
        {
            Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(600), 0),
            Text = "Un modello per riga. Esempi:\r\n" +
                   "  .thumbnails/        → esclude ogni cartella con questo nome\r\n" +
                   "  *.tmp               → esclude i file con questo nome/estensione\r\n" +
                   "  WhatsApp/Media/.Statuses/   → con '/' interno: percorso dalla cartella scelta sul telefono\r\n" +
                   "  ** = qualsiasi sottocartella, * = qualsiasi testo, ? = un carattere. Le righe che iniziano con # sono commenti.\r\n\r\n" +
                   "Predefinite: .thumbnails/ (miniature rigenerate da Android), .trashed-* (cestino), .pending-* (file in scrittura). " +
                   "Le regole valgono dalla cartella scelta sul telefono: scegliendo direttamente /sdcard/Pictures/.thumbnails i file NON sono esclusi. " +
                   "I file esclusi restano visibili con il filtro \"Esclusi\".",
        };
        var reset = new Button { Text = "Ripristina predefinite", AutoSize = true };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Annulla", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { cancel, ok, reset });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(help, 0, 0);
        root.Controls.Add(_text, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        _text.Lines = patterns.ToArray();
        reset.Click += (_, _) => _text.Lines = SyncOptions.DefaultExcludes;
    }
}

/// <summary>Fine esecuzione: propone le operazioni successive sull'elenco già acquisito (senza nuova scansione).</summary>
internal sealed class PostRunDialog : Form
{
    public enum Choice { Close, RetryFailed, AlignDates }

    public Choice Result { get; private set; } = Choice.Close;
    public DateAlignMode AlignMode => _cmbAlign.SelectedIndex switch { 0 => DateAlignMode.Quick, 1 => DateAlignMode.Full, _ => DateAlignMode.SizeOnly };

    private readonly ComboBox _cmbAlign = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };

    public PostRunDialog(string summary, int toRetry, int dateOnly, DateAlignMode lastMode)
    {
        Text = "Altre operazioni sull'elenco acquisito";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(12), WrapContents = false };
        root.Controls.Add(new Label { Text = summary, AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(640), 0), Margin = new Padding(3, 3, 3, 12) });
        root.Controls.Add(new Label { Text = "L'elenco di telefono e PC è già in memoria: le operazioni seguenti non richiedono una nuova scansione.", AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(640), 0), ForeColor = SystemColors.GrayText });

        if (toRetry > 0)
        {
            var b = new Button { Text = $"Riprova i {Format.Number(toRetry)} file non copiati", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
            b.Click += (_, _) => { Result = Choice.RetryFailed; DialogResult = DialogResult.OK; };
            root.Controls.Add(b);
        }
        if (dateOnly > 0)
        {
            root.Controls.Add(new Label
            {
                Text = $"{Format.Number(dateOnly)} file hanno stesso nome e stessa dimensione ma data diversa (copiati in passato senza conservare la data).",
                AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(640), 0), Margin = new Padding(3, 12, 3, 3),
            });
            _cmbAlign.Items.AddRange(new object[]
            {
                "Verifica rapida (primi e ultimi 64 KB; non vede differenze solo a metà file) e allinea le date",
                "Verifica MD5 completa e allinea le date (più lenta)",
                "Allinea le date senza verifica (solo nome e dimensione)",
            });
            _cmbAlign.SelectedIndex = lastMode switch { DateAlignMode.Full => 1, DateAlignMode.SizeOnly => 2, _ => 0 };
            root.Controls.Add(_cmbAlign);
            var b = new Button { Text = "Allinea le date (solo per questa operazione)", AutoSize = true };
            b.Click += (_, _) => { Result = Choice.AlignDates; DialogResult = DialogResult.OK; };
            root.Controls.Add(b);
        }
        var close = new Button { Text = "Chiudi", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(3, 16, 3, 3) };
        root.Controls.Add(close);
        CancelButton = close;
        Controls.Add(root);
    }
}
