using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AdbSync.Core;
using AdbSync.Core.Adb;

namespace AdbSync.Gui;

internal sealed class MainForm : Form
{
    private static readonly string AppVersion = "0.1.2";

    // --- stato ---
    private readonly IReadOnlyList<string> _migration = AppPaths.MigrateLegacy(); // prima di caricare le impostazioni
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SyncLogger _log = new(SyncLogger.DefaultLogDirectory);
    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private AdbEnvironment? _env;
    private SyncPlan? _plan;
    /// <summary>Elenco acquisito (telefono + PC): riusato per ricalcolare il piano e per le operazioni successive.</summary>
    private ComparisonSession? _session;
    /// <summary>Scelte manuali ("Copia"/"Non copiare") sull'elenco acquisito: sopravvivono ai ricalcoli del piano.</summary>
    private readonly Dictionary<string, ItemAction> _manualActions = new(StringComparer.Ordinal);
    private SyncEngine? _engine;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private volatile string? _compareStatus;
    private readonly Stopwatch _compareWatch = new();
    private List<SyncItem> _view = new();
    private int _sortColumn = 2;
    private bool _sortDescending;

    // --- controlli ---
    private readonly ComboBox _cmbDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _btnRefresh = new() { Text = "Aggiorna", AutoSize = true };
    private readonly TextBox _txtRemote = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBrowseRemote = new() { Text = "Sfoglia...", AutoSize = true };
    private readonly TextBox _txtLocal = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBrowseLocal = new() { Text = "Sfoglia...", AutoSize = true };

    private readonly CheckBox _chkSizeOnly = new() { Text = "Confronta solo la dimensione", AutoSize = true };
    private readonly CheckBox _chkDst = new() { Text = "Ignora differenze di 1 ora (ora legale)", AutoSize = true };
    private readonly CheckBox _chkVerify = new() { Text = "Verifica MD5 (più lento)", AutoSize = true };
    private readonly CheckBox _chkOverwrite = new() { Text = "Sovrascrivi i conflitti", AutoSize = true };
    private readonly NumericUpDown _numParallel = new() { Minimum = 1, Maximum = 4, Width = 45 };
    private readonly NumericUpDown _numStall = new() { Minimum = 1, Maximum = 600, Width = 55 };
    private readonly ComboBox _cmbAlign = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
    private readonly NumericUpDown _numRetries = new() { Minimum = 0, Maximum = 20, Width = 45 };
    private readonly Button _btnExcludes = new() { Text = "Esclusioni...", AutoSize = true };
    private readonly CheckBox _chkDiagnostic = new() { Text = "Modalità diagnostica (elenchi completi nella cartella logs)", AutoSize = true };

    private readonly Button _btnCompare = new() { Text = "① Confronta", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnSync = new() { Text = "② Sincronizza", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Padding = new Padding(8, 2, 8, 2), Enabled = false };
    private readonly Button _btnCancel = new() { Text = "Annulla", AutoSize = true, Enabled = false, Padding = new Padding(8, 2, 8, 2) };
    private readonly Button _btnAdb = new() { Text = "adb...", AutoSize = true };
    private readonly Label _lblAdb = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView _lvFiles = new()
    {
        Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, GridLines = false,
        HideSelection = false, VirtualListSize = 0,
    };
    private readonly FlowLayoutPanel _filters = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private readonly Dictionary<ItemCategory, CheckBox> _filterChecks = new();
    private readonly CheckBox _chkOnlyErrors = new() { Text = "Solo errori", AutoSize = true };
    private readonly TextBox _txtLog = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font("Consolas", 9), BackColor = SystemColors.Window,
    };
    private readonly CheckBox _chkDetailedLog = new() { Text = "Mostra dettagli (ogni file copiato)", AutoSize = true };
    private readonly ListView _lvErrors = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly TabPage _tabErrors = new("Errori");

    private readonly Label _lblPhase = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Text = "Pronto" };
    private readonly Label _lblStatusMsg = new() { AutoSize = true, ForeColor = Color.DarkOrange, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly ProgressBar _pbTotal = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 22 };
    private readonly Label _lblTotal = new() { AutoSize = true };
    private readonly Label _lblCounts = new() { AutoSize = true };
    private readonly ListView _lvActive = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = 92 };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 300 };

    public MainForm()
    {
        Text = $"AdbSync {AppVersion} — copia incrementale Android → PC via ADB";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(S(1180), S(820));
        MinimumSize = new Size(S(900), S(640));

        BuildLayout();
        LoadSettingsIntoControls();
        WireEvents();

        _log.EntryWritten += e => _pendingLog.Enqueue(e);
        _log.Info($"AdbSync {AppVersion} — dati in {(AppPaths.IsPortable ? "cartella del programma (portable)" : "AppData (cartella del programma non scrivibile)")}: {Path.GetDirectoryName(AppPaths.DataDirectory)}");
        foreach (var m in _migration) _log.Info(m);
        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();
        Shown += async (_, _) => await InitAdbAsync(interactive: true);
    }

    // =====================================================================  LAYOUT

    private void BuildLayout()
    {
        var root = SingleColumn(new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Padding = new Padding(8) });
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // sorgente/destinazione
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // opzioni
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // pulsanti
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // schede | stato (divisore trascinabile)

        // --- sorgente / destinazione ---
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.Controls.Add(MakeLabel("Telefono:"), 0, 0);
        paths.Controls.Add(_cmbDevice, 1, 0);
        paths.Controls.Add(_btnRefresh, 2, 0);
        paths.Controls.Add(MakeLabel("Cartella sul telefono:"), 0, 1);
        paths.Controls.Add(_txtRemote, 1, 1);
        paths.Controls.Add(_btnBrowseRemote, 2, 1);
        paths.Controls.Add(MakeLabel("Cartella sul PC:"), 0, 2);
        paths.Controls.Add(_txtLocal, 1, 2);
        paths.Controls.Add(_btnBrowseLocal, 2, 2);
        var grpPaths = new GroupBox { Text = "Origine (telefono)  →  Destinazione (PC)   ·   modalità: Aggiorna (copia nuovi e modificati, non cancella mai nulla)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        grpPaths.Controls.Add(paths);
        root.Controls.Add(grpPaths, 0, 0);

        // --- opzioni ---
        _numParallel.Width = S(45);
        _numStall.Width = S(60);
        _numRetries.Width = S(45);
        // Due righe fisse: nessuna opzione può finire fuori dalla finestra.
        var row1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row1.Controls.Add(_chkSizeOnly);
        row1.Controls.Add(_chkDst);
        row1.Controls.Add(_chkOverwrite);
        row1.Controls.Add(_chkVerify);
        var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row2.Controls.Add(MakeLabel("Trasferimenti paralleli:"));
        row2.Controls.Add(_numParallel);
        row2.Controls.Add(MakeLabel("Timeout stallo (s):"));
        row2.Controls.Add(_numStall);
        row2.Controls.Add(MakeLabel("Tentativi extra:"));
        row2.Controls.Add(_numRetries);
        row2.Controls.Add(_btnExcludes);
        row2.Controls.Add(_chkDiagnostic);
        _chkDiagnostic.Margin = new Padding(12, 6, 3, 3);
        var row3 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row3.Controls.Add(MakeLabel("File con stessa dimensione ma data diversa:"));
        _cmbAlign.Items.AddRange(new object[]
        {
            "non toccare",
            "allinea le date (solo nome e dimensione)",
            "verifica rapida (primi e ultimi 64 KB) e allinea le date",
            "verifica MD5 completa e allinea le date",
        });
        _cmbAlign.Width = S(380);
        row3.Controls.Add(_cmbAlign);
        var opts = SingleColumn(new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 3 });
        opts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        opts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        opts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        opts.Controls.Add(row1, 0, 0);
        opts.Controls.Add(row2, 0, 1);
        opts.Controls.Add(row3, 0, 2);
        var grpOpts = new GroupBox { Text = "Opzioni", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        grpOpts.Controls.Add(opts);
        root.Controls.Add(grpOpts, 0, 1);

        var tip = new ToolTip { AutoPopDelay = 20000 };
        tip.SetToolTip(_chkSizeOnly, "Utile se i file sul PC sono stati copiati in passato (es. via MTP) senza conservare la data originale.");
        tip.SetToolTip(_chkDst, "Su dischi FAT/exFAT le date possono risultare spostate di un'ora con il cambio di ora legale.");
        tip.SetToolTip(_chkOverwrite, "Conflitto = il file sul PC è più recente e diverso. Normalmente non viene toccato.");
        tip.SetToolTip(_chkVerify, "Calcola l'MD5 sul telefono e sul PC per ogni file copiato. Garantisce l'integrità ma rallenta.");
        tip.SetToolTip(_numStall, "Se per questi secondi non arriva alcun byte il file è considerato bloccato: viene rimesso in fondo alla coda (senza attese) e ritentato dopo gli altri.\nValori molto bassi (1-3 s) aiutano con molti file bloccati, ma un file grande con pause normali potrebbe ripartire più volte.");
        tip.SetToolTip(_cmbAlign, "Per i file presenti su entrambi con stesso nome e dimensione ma data diversa (es. copiati in passato via MTP).\nRapida: esatta per file fino a 128 KB; per quelli più grandi controlla inizio e fine.\nMD5: legge tutto il file su telefono e PC.");
        tip.SetToolTip(_chkDiagnostic, "A ogni confronto salva in logs\\diagnostica_<data> l'elenco completo dei file del telefono, del PC e del piano con il motivo di ogni decisione.");

        // --- pulsanti ---
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 6 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_btnCompare, 0, 0);
        actions.Controls.Add(_btnSync, 1, 0);
        actions.Controls.Add(_btnCancel, 2, 0);
        actions.Controls.Add(new Label(), 3, 0);
        actions.Controls.Add(_lblAdb, 4, 0);
        actions.Controls.Add(_btnAdb, 5, 0);
        root.Controls.Add(actions, 0, 2);

        // --- scheda File ---
        AddColumns(_lvFiles, ("Esito", 110), ("Categoria", 120), ("Percorso", 420), ("Dim. telefono", 95), ("Data telefono", 135), ("Dim. PC", 95), ("Data PC", 135), ("Note", 300));
        foreach (var (cat, label, on) in new[]
                 {
                     (ItemCategory.New, "Nuovi", true), (ItemCategory.Changed, "Modificati", true), (ItemCategory.Conflict, "Conflitti", true),
                     (ItemCategory.DateOnly, "Solo data diversa", true), (ItemCategory.Collision, "Collisioni", true),
                     (ItemCategory.Blocked, "Non copiabili", true),
                     (ItemCategory.Identical, "Identici", false), (ItemCategory.OnlyOnPc, "Solo sul PC", false),
                     (ItemCategory.Excluded, "Esclusi", false),
                 })
        {
            var cb = new CheckBox { Text = label, Checked = on, AutoSize = true, Tag = label, ForeColor = CategoryColor(cat) };
            cb.CheckedChanged += (_, _) => RefreshView();
            _filterChecks[cat] = cb;
            _filters.Controls.Add(cb);
        }
        _chkOnlyErrors.CheckedChanged += (_, _) => RefreshView();
        _filters.Controls.Add(_chkOnlyErrors);

        var filesPanel = SingleColumn(new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 });
        filesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        filesPanel.Controls.Add(_filters, 0, 0);
        filesPanel.Controls.Add(_lvFiles, 0, 1);
        var tabFiles = new TabPage("File") { Padding = new Padding(4) };
        tabFiles.Controls.Add(filesPanel);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copia anche questi file", null, (_, _) => SetActionOnSelection(ItemAction.Copy));
        menu.Items.Add("Non copiare questi file", null, (_, _) => SetActionOnSelection(ItemAction.Skip));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Apri cartella sul PC", null, (_, _) => OpenSelectedInExplorer());
        _lvFiles.ContextMenuStrip = menu;

        // --- scheda Log ---
        var logPanel = SingleColumn(new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 });
        logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var btnOpenLogs = new Button { Text = "Apri cartella log e report", AutoSize = true };
        btnOpenLogs.Click += (_, _) => OpenFolder(SyncLogger.DefaultLogDirectory);
        logBar.Controls.Add(_chkDetailedLog);
        logBar.Controls.Add(btnOpenLogs);
        logPanel.Controls.Add(logBar, 0, 0);
        logPanel.Controls.Add(_txtLog, 0, 1);
        var tabLog = new TabPage("Log") { Padding = new Padding(4) };
        tabLog.Controls.Add(logPanel);

        // --- scheda Errori ---
        AddColumns(_lvErrors, ("Percorso", 480), ("Tentativi", 70), ("Errore", 520));
        _tabErrors.Padding = new Padding(4);
        _tabErrors.Controls.Add(_lvErrors);

        _tabs.TabPages.Add(tabFiles);
        _tabs.TabPages.Add(tabLog);
        _tabs.TabPages.Add(_tabErrors);
        _split.Panel1.Controls.Add(_tabs);
        root.Controls.Add(_split, 0, 3);

        // --- stato ---
        var status = SingleColumn(new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5 });
        status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
        status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // l'elenco dei trasferimenti prende lo spazio restante
        var phaseRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        phaseRow.Controls.Add(_lblPhase);
        phaseRow.Controls.Add(_lblStatusMsg);
        status.Controls.Add(phaseRow, 0, 0);
        status.Controls.Add(_pbTotal, 0, 1);
        status.Controls.Add(_lblTotal, 0, 2);
        status.Controls.Add(_lblCounts, 0, 3);
        AddColumns(_lvActive, ("#", 30), ("File in trasferimento", 560), ("Avanzamento", 230), ("Velocità", 100), ("Tentativo", 80));
        status.Controls.Add(_lvActive, 0, 4);
        var grpStatus = new GroupBox { Text = "Stato   (trascina la linea sopra per ridimensionare)", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        grpStatus.Controls.Add(status);
        _split.Panel2.Controls.Add(grpStatus);

        Controls.Add(root);
    }

    /// <summary>Converte unità logiche (96 dpi) in pixel reali per schermi con scaling 125/150%.</summary>
    private int S(int logical) => LogicalToDeviceUnits(logical);

    private static TableLayoutPanel SingleColumn(TableLayoutPanel t)
    {
        t.ColumnCount = 1;
        t.ColumnStyles.Clear();
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    private void AddColumns(ListView lv, params (string Text, int Width)[] cols)
    {
        foreach (var (t, w) in cols) lv.Columns.Add(t, S(w));
    }

    private void LoadSettingsIntoControls()
    {
        _txtRemote.Text = _settings.RemotePath;
        _txtLocal.Text = _settings.LocalPath;
        _chkSizeOnly.Checked = _settings.SizeOnly;
        _chkDst.Checked = _settings.IgnoreDst;
        _chkVerify.Checked = _settings.VerifyHash;
        _chkOverwrite.Checked = _settings.OverwriteConflicts;
        _numParallel.Value = Math.Clamp(_settings.Parallel, 1, 4);
        _numStall.Value = Math.Clamp(_settings.StallSeconds, 5, 600);
        _numRetries.Value = Math.Clamp(_settings.Retries, 0, 20);
        _chkDetailedLog.Checked = _settings.ShowDetailedLog;
        _chkDiagnostic.Checked = _settings.Diagnostic;
        _cmbAlign.SelectedIndex = (int)_settings.AlignSameSizeDates;
    }

    private void SaveControlsIntoSettings()
    {
        _settings.RemotePath = _txtRemote.Text.Trim();
        _settings.LocalPath = _txtLocal.Text.Trim();
        _settings.SizeOnly = _chkSizeOnly.Checked;
        _settings.IgnoreDst = _chkDst.Checked;
        _settings.VerifyHash = _chkVerify.Checked;
        _settings.OverwriteConflicts = _chkOverwrite.Checked;
        _settings.Parallel = (int)_numParallel.Value;
        _settings.StallSeconds = (int)_numStall.Value;
        _settings.Retries = (int)_numRetries.Value;
        _settings.ShowDetailedLog = _chkDetailedLog.Checked;
        _settings.Diagnostic = _chkDiagnostic.Checked;
        _settings.AlignSameSizeDates = (DateAlignMode)Math.Max(0, _cmbAlign.SelectedIndex);
        if (_split.IsHandleCreated) _settings.SplitterDistance = _split.Height - _split.SplitterDistance; // altezza del riquadro Stato
        if (_cmbDevice.SelectedItem is DeviceInfo d) _settings.LastSerial = d.Serial;
        _settings.Save();
    }

    private void WireEvents()
    {
        _btnRefresh.Click += async (_, _) => await RefreshDevicesAsync();
        _btnBrowseRemote.Click += (_, _) => BrowseRemote();
        _btnBrowseLocal.Click += (_, _) => BrowseLocal();
        _btnExcludes.Click += (_, _) =>
        {
            using var dlg = new ExcludesDialog(_settings.Excludes);
            if (dlg.ShowDialog(this) == DialogResult.OK) { _settings.Excludes = dlg.Patterns; _settings.Save(); OptionsChanged(); }
        };
        _btnCompare.Click += async (_, _) => await CompareAsync();
        _btnSync.Click += async (_, _) => await SyncAsync();
        _btnCancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            _btnCancel.Enabled = false;
            _lblStatusMsg.Text = "Annullamento in corso: chiusura pulita dei trasferimenti...";
        };
        _btnAdb.Click += (_, _) => ShowAdbMenu();

        // Qualsiasi modifica a percorsi/opzioni di confronto invalida il piano.
        _txtRemote.TextChanged += (_, _) => InvalidatePlan(dropSession: true);
        _txtLocal.TextChanged += (_, _) => InvalidatePlan(dropSession: true);
        _cmbDevice.SelectedIndexChanged += (_, _) => InvalidatePlan(dropSession: true);
        // le opzioni di confronto ricalcolano il piano dall'elenco già acquisito (nessuna scansione)
        _chkSizeOnly.CheckedChanged += (_, _) => OptionsChanged();
        _chkDst.CheckedChanged += (_, _) => OptionsChanged();
        _chkOverwrite.CheckedChanged += (_, _) => OptionsChanged();
        _cmbAlign.SelectedIndexChanged += (_, _) => OptionsChanged();
        _numParallel.ValueChanged += (_, _) => EnsureStatusHeight();
        Shown += (_, _) => InitSplitter();

        _lvFiles.RetrieveVirtualItem += (_, e) => e.Item = BuildListItem(e.ItemIndex);
        _lvFiles.ColumnClick += (_, e) =>
        {
            _sortDescending = _sortColumn == e.Column && !_sortDescending;
            _sortColumn = e.Column;
            RefreshView();
        };
        _lvFiles.DoubleClick += (_, _) => OpenSelectedInExplorer();
        _chkDetailedLog.CheckedChanged += (_, _) => _settings.ShowDetailedLog = _chkDetailedLog.Checked;

        FormClosing += OnFormClosing;
    }

    // =====================================================================  ADB / DISPOSITIVI

    private async Task InitAdbAsync(bool interactive)
    {
        string? adb = AdbEnvironment.Locate(_settings.AdbPath);
        if (adb is null)
        {
            _lblAdb.Text = "adb non trovato";
            if (!interactive) return;
            var r = MessageBox.Show(this,
                "Per comunicare con il telefono serve adb (Android platform-tools di Google).\n\n" +
                "Scaricarlo ora automaticamente dal sito ufficiale Google (circa 7 MB)?\n\n" +
                "Sì = scarica  ·  No = indica tu il percorso di adb.exe",
                "adb non trovato", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) adb = await DownloadAdbAsync();
            else if (r == DialogResult.No) adb = PickAdbExe();
            if (adb is null) return;
        }

        try
        {
            SetBusy(true, "Avvio di adb...");
            _env = await Task.Run(() => AdbEnvironment.StartAsync(adb, CancellationToken.None));
            _settings.AdbPath = adb;
            _lblAdb.Text = "adb: " + adb;
            _log.Info("adb in uso: " + adb);
        }
        catch (Exception ex)
        {
            _env = null;
            _lblAdb.Text = "adb non avviato";
            _log.Error(ex.Message);
            if (interactive) MessageBox.Show(this, ex.Message, "Errore adb", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
        await RefreshDevicesAsync();
    }

    private async Task<string?> DownloadAdbAsync()
    {
        try
        {
            SetBusy(true, "Download platform-tools da Google...");
            return await Task.Run(() => AdbEnvironment.DownloadPlatformToolsAsync(new InlineProgress<string>(m => _log.Info(m)), CancellationToken.None));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Download non riuscito: " + ex.Message +
                "\n\nPuoi scaricare manualmente le platform-tools da https://developer.android.com/tools/releases/platform-tools e indicare adb.exe.",
                "adb", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private string? PickAdbExe()
    {
        using var dlg = new OpenFileDialog { Title = "Seleziona adb.exe", Filter = "adb.exe|adb.exe|Eseguibili|*.exe", CheckFileExists = true };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    private void ShowAdbMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Scarica / aggiorna platform-tools ufficiali Google", null, async (_, _) =>
        {
            var p = await DownloadAdbAsync();
            if (p is not null) { _settings.AdbPath = p; await InitAdbAsync(false); }
        });
        m.Items.Add("Scegli adb.exe...", null, async (_, _) =>
        {
            var p = PickAdbExe();
            if (p is not null) { _settings.AdbPath = p; await InitAdbAsync(true); }
        });
        m.Items.Add("Riavvia collegamento adb", null, async (_, _) => await InitAdbAsync(true));
        m.Items.Add("Arresta il server adb (libera la cartella del programma)", null, (_, _) =>
        {
            if (_busy) return;
            if (_env is not null) AdbEnvironment.KillServer(_env.AdbPath);
            _env = null;
            _lblAdb.Text = "adb arrestato — premi Aggiorna per riavviarlo";
            _log.Info("Server adb arrestato.");
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Apri cartella log e report", null, (_, _) => OpenFolder(SyncLogger.DefaultLogDirectory));
        m.Show(_btnAdb, new Point(0, _btnAdb.Height));
    }

    private async Task RefreshDevicesAsync()
    {
        if (_env is null) { await InitAdbAsync(true); return; }
        try
        {
            var devices = await Task.Run(() => _env.GetDevicesAsync(CancellationToken.None));
            string? keep = (_cmbDevice.SelectedItem as DeviceInfo)?.Serial ?? _settings.LastSerial;
            _cmbDevice.Items.Clear();
            foreach (var d in devices) _cmbDevice.Items.Add(d);
            if (devices.Count == 0)
            {
                _cmbDevice.Items.Add("Nessun telefono: collega il cavo, attiva 'Debug USB' e premi Aggiorna");
                _cmbDevice.SelectedIndex = 0;
                return;
            }
            var sel = devices.FirstOrDefault(d => d.Serial == keep) ?? devices.FirstOrDefault(d => d.IsOnline) ?? devices[0];
            _cmbDevice.SelectedItem = sel;
            foreach (var d in devices.Where(d => !d.IsOnline)) _log.Warn($"{d.Model}: {d.StateLabel}");
        }
        catch (Exception ex)
        {
            _log.Error("Elenco dispositivi non disponibile: " + ex.Message);
        }
    }

    private IRemoteDevice? GetSelectedDevice(bool showErrors)
    {
        if (_env is null)
        {
            if (showErrors) MessageBox.Show(this, "adb non è attivo. Usa il pulsante 'adb...'.", "AdbSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (_cmbDevice.SelectedItem is not DeviceInfo d)
        {
            if (showErrors) MessageBox.Show(this, "Nessun telefono selezionato.\n\nCollega il telefono, attiva 'Debug USB' nelle Opzioni sviluppatore, conferma l'autorizzazione sul telefono e premi Aggiorna.", "AdbSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (!d.IsOnline)
        {
            if (showErrors) MessageBox.Show(this, $"{d.Model}: {d.StateLabel}.\n\nDopo aver risolto premi Aggiorna.", "AdbSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return _env.OpenDevice(d.Serial, d.ToString());
    }

    private void BrowseRemote()
    {
        var dev = GetSelectedDevice(true);
        if (dev is null) return;
        using var dlg = new RemoteFolderDialog(dev, _txtRemote.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtRemote.Text = dlg.SelectedPath;
    }

    private void BrowseLocal()
    {
        using var dlg = new FolderBrowserDialog { Description = "Cartella di destinazione sul PC", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(_txtLocal.Text)) dlg.InitialDirectory = _txtLocal.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtLocal.Text = dlg.SelectedPath;
    }

    // =====================================================================  CONFRONTO / COPIA

    private SyncOptions CurrentOptions()
    {
        SaveControlsIntoSettings();
        return _settings.ToOptions();
    }

    private async Task CompareAsync()
    {
        var dev = GetSelectedDevice(true);
        if (dev is null) return;
        string remote = _txtRemote.Text.Trim();
        string local = _txtLocal.Text.Trim();
        if (remote.Length == 0) { MessageBox.Show(this, "Indica la cartella sul telefono.", "AdbSync"); return; }
        if (local.Length == 0 || !Path.IsPathFullyQualified(local)) { MessageBox.Show(this, "Indica una cartella valida sul PC (percorso completo, es. D:\\Backup\\Telefono).", "AdbSync"); return; }

        var options = CurrentOptions();
        _cts = new CancellationTokenSource();
        _plan = null;
        _view = new List<SyncItem>();
        _lvFiles.VirtualListSize = 0;
        ResetStatusPanel();
        SetBusy(true, "Scansione in corso...");
        _pbTotal.Style = ProgressBarStyle.Marquee;
        _tabs.SelectedIndex = 0;
        var sw = Stopwatch.StartNew();
        _compareWatch.Restart();
        try
        {
            _session = null;
            _manualActions.Clear(); // nuova scansione: si riparte dalle scelte predefinite
            var token = _cts.Token;
            var session = await Task.Run(() => SyncEngine.ScanAsync(dev, remote, local, options, _log, s => _compareStatus = s, token));
            var plan = await Task.Run(() => SyncEngine.PlanFromSession(session, options, _log, writeDiagnostics: true));
            _txtRemote.Text = session.RemoteRoot; // normalizzato (TextChanged invaliderebbe: si riassegna dopo)
            _session = session;
            _plan = plan;
            UpdateFilterCounts();
            RefreshView();
            _lblPhase.Text = $"Confronto completato in {Format.Duration(sw.Elapsed)}: {plan.WorkSummary}.";
            _lblTotal.Text = CountsText(plan);
            if (plan.NotCopiable > 0)
            {
                _lblStatusMsg.Text = $"Attenzione: {Format.Number(plan.NotCopiable)} elementi del telefono NON copiabili (permesso negato / collisioni): vedi scheda Errori.";
                FillErrors(plan);
            }
            int excludedAll = plan.ExcludedCount + plan.ExcludedLocalCount;
            if (excludedAll > 0 && plan.NotCopiable == 0)
                _lblStatusMsg.Text = $"{Format.Number(excludedAll)} file esclusi dalle regole (es. miniature .thumbnails): non confrontati. Per vederli spunta il filtro \"Esclusi\"; per includerli usa Esclusioni….";
            int dateOnly = plan.Count(ItemCategory.DateOnly);
            if (dateOnly > 50 && !options.CompareMode.Equals(CompareMode.SizeOnly))
                _log.Warn($"{dateOnly} file hanno stessa dimensione ma data diversa (PC più recente): probabilmente copiati in passato senza conservare la data. Non vengono ricopiati.");
        }
        catch (OperationCanceledException)
        {
            _lblPhase.Text = "Confronto annullato.";
            _session = null;
        }
        catch (Exception ex)
        {
            _lblPhase.Text = ex is IncompleteScanException ? "✖ Scansione del telefono incompleta: confronto fermato." : "✖ Confronto non riuscito.";
            _lblPhase.ForeColor = Color.Firebrick;
            _log.Error(ex.Message);
            MessageBox.Show(this, ex.Message, "Confronto non riuscito", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _compareStatus = null;
            _compareWatch.Stop();
            _pbTotal.Style = ProgressBarStyle.Continuous;
            _pbTotal.Value = 0;
            SetBusy(false, null);
        }
    }

    private async Task SyncAsync(bool confirm = true, DateAlignMode? alignOverride = null)
    {
        if (_plan is null) { await CompareAsync(); if (_plan is null) return; }
        var plan = _plan;
        if (!plan.HasWork) { MessageBox.Show(this, "Niente da fare: la cartella sul PC è già allineata.", "AdbSync"); return; }
        var dev = GetSelectedDevice(true);
        if (dev is null) return;

        var age = DateTime.Now - plan.ScannedAt;
        string ageNote = age.TotalMinutes > 60
            ? $"\n\nL'elenco è stato acquisito {Format.Duration(age)} fa. Prima di scrivere, ogni file sul PC viene comunque ricontrollato; " +
              "se nel frattempo sono cambiati file sul telefono, conviene rifare ① Confronta."
            : "";
        if (confirm && MessageBox.Show(this,
                $"Da fare: {plan.WorkSummary}.\n\nda  {dev.DisplayName}:{plan.RemoteRoot}\na   {plan.LocalRoot}\n\nNessun file verrà cancellato.{ageNote}",
                "Conferma sincronizzazione", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        var options = CurrentOptions();
        if (alignOverride is { } am) options.AlignSameSizeDates = am;
        _cts = new CancellationTokenSource();
        ResetStatusPanel();
        var engine = _engine = new SyncEngine(dev, options, _log);
        var token = _cts.Token;
        SetBusy(true, "Copia in corso...");
        KeepAwake(true);
        SyncResult? result = null;
        try
        {
            result = await Task.Run(() => engine.RunAsync(plan, token));
        }
        catch (Exception ex)
        {
            _log.Error("Errore imprevisto: " + ex);
            MessageBox.Show(this, ex.Message, "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            KeepAwake(false);
            UiTick();        // ultimo aggiornamento dei contatori (il tempo è fermo: FinishedAt)
            _engine = null;  // il riquadro Stato resta sul riepilogo finale e non si aggiorna più
            SetBusy(false, null);
            _btnSync.Enabled = false; // per riprendere: nuovo confronto (veloce) e poi sincronizza
        }

        if (result is null) return;
        _lblPhase.Text = result.FullySucceeded ? "✔ " + result.Summary : "✖ " + result.Summary;
        _lblPhase.ForeColor = result.FullySucceeded ? Color.DarkGreen : Color.Firebrick;
        FillErrors(plan);
        _lvFiles.Invalidate();
        if (!result.FullySucceeded && _lvErrors.Items.Count > 0) _tabs.SelectedTab = _tabErrors;

        // L'elenco in memoria viene aggiornato con ciò che è stato scritto: il nuovo piano è già quello reale.
        if (_session is not null)
        {
            _session.ApplyResults(plan);
            var session = _session;
            var next = WithManualActions(await Task.Run(() => SyncEngine.PlanFromSession(session, _settings.ToOptions(), _log, writeDiagnostics: false)));
            _plan = next;
            UpdateFilterCounts();
            RefreshView();
            FillErrors(plan); // gli errori restano quelli dell'esecuzione appena conclusa
            _btnSync.Enabled = next.HasWork;
        }

        string report = (result.ReportPath is not null ? "\n\nReport: " + result.ReportPath : "") +
                        (result.RenamedLogPath is not null ? $"\nNomi convertiti per Windows ({Format.Number(result.RenamedCount)}): " + result.RenamedLogPath : "");
        int toRetry = _plan?.CountToCopy ?? 0;
        int dateOnly = _plan?.Count(ItemCategory.DateOnly) ?? 0;
        if (_session is null || (toRetry == 0 && dateOnly == 0))
        {
            MessageBox.Show(this, result.Summary + report, "AdbSync",
                MessageBoxButtons.OK, result.FullySucceeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return;
        }
        using var dlg = new PostRunDialog(result.Summary + report, toRetry, dateOnly, _settings.AlignSameSizeDates);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.Result == PostRunDialog.Choice.AlignDates)
        {
            // Solo per questa operazione: l'opzione nella finestra principale non cambia.
            var mode = dlg.AlignMode;
            var session = _session;
            var opts = CurrentOptions();
            opts.AlignSameSizeDates = mode;
            SetBusy(true, "Preparazione dell'allineamento date dall'elenco acquisito...");
            try
            {
                _plan = await Task.Run(() => SyncEngine.PlanFromSession(session, opts, _log, writeDiagnostics: false));
            }
            finally { SetBusy(false, null); }
            // solo l'allineamento: niente copie in questo giro
            foreach (var i in _plan.Items.Where(i => i.Action == ItemAction.Copy)) i.Action = ItemAction.Skip;
            UpdateFilterCounts();
            RefreshView();
            await SyncAsync(confirm: false, alignOverride: mode);
            return;
        }
        await SyncAsync(confirm: false);
    }

    /// <summary>Riapplica al piano ricalcolato le scelte manuali fatte sull'elenco acquisito.</summary>
    private SyncPlan WithManualActions(SyncPlan plan)
    {
        if (_manualActions.Count == 0) return plan;
        foreach (var i in plan.Items)
        {
            if (i.Remote is null || i.Category is ItemCategory.Collision or ItemCategory.Blocked or ItemCategory.Excluded) continue;
            if (!_manualActions.TryGetValue(i.RelativePath, out var a)) continue;
            // "Copia" forzata vale solo finché il file è ancora diverso (dopo la copia è identico: niente ricopia)
            if (a == ItemAction.Copy && i.Category == ItemCategory.Identical) continue;
            if (a == ItemAction.Skip || i.Action == ItemAction.Skip) i.Action = a;
        }
        return plan;
    }

    private static string CountsText(SyncPlan p) =>
        $"Elenco: telefono {Format.Number(p.RemoteFileCount)} file e {Format.Number(p.RemoteDirCount)} cartelle  ·  " +
        $"PC {Format.Number(p.LocalFileCount)} file e {Format.Number(p.LocalDirCount)} cartelle  ·  " +
        (p.ExcludedCount + p.ExcludedLocalCount > 0 ? $"esclusi {Format.Number(p.ExcludedCount + p.ExcludedLocalCount)}  ·  " : "") +
        $"acquisito alle {p.ScannedAt:HH:mm}";

    /// <summary>Opzione di confronto cambiata: si ricalcola il piano dall'elenco già acquisito, se valido.</summary>
    private async void OptionsChanged()
    {
        if (_busy) return;
        if (_session is null || _cmbDevice.SelectedItem is not DeviceInfo d ||
            !_session.Matches(d.Serial, _txtRemote.Text.Trim(), _txtLocal.Text.Trim()))
        {
            InvalidatePlan(dropSession: true);
            return;
        }
        await ReplanAsync();
    }

    private async Task ReplanAsync()
    {
        if (_session is null) return;
        var session = _session;
        var options = CurrentOptions();
        SetBusy(true, "Ricalcolo del piano dall'elenco acquisito (nessuna scansione)...");
        try
        {
            _plan = WithManualActions(await Task.Run(() => SyncEngine.PlanFromSession(session, options, _log, writeDiagnostics: false)));
            UpdateFilterCounts();
            RefreshView();
            _lblPhase.Text = $"Piano ricalcolato dall'elenco delle {session.ScannedAt:HH:mm}: {_plan.WorkSummary}.";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void FillErrors(SyncPlan plan)
    {
        _lvErrors.BeginUpdate();
        _lvErrors.Items.Clear();
        foreach (var d in plan.Directories.Where(d => d.Error is not null))
        {
            var li = new ListViewItem("[cartella] " + (d.IsRoot ? plan.LocalRoot : d.LocalRelativePath)) { ForeColor = Color.Firebrick };
            li.SubItems.Add("");
            li.SubItems.Add(d.Error);
            _lvErrors.Items.Add(li);
        }
        foreach (var p in plan.PermissionDenied)
        {
            var li = new ListViewItem("[telefono] " + p) { ForeColor = Color.Firebrick };
            li.SubItems.Add("");
            li.SubItems.Add("Android non permette di leggerlo: non copiato");
            _lvErrors.Items.Add(li);
        }
        foreach (var i in plan.Items.Where(i => i.Status is ItemStatus.Failed or ItemStatus.NotProcessed or ItemStatus.ContentDiffers || i.Category is ItemCategory.Collision or ItemCategory.Blocked))
        {
            var li = new ListViewItem(i.RelativePath)
            {
                ForeColor = i.Status == ItemStatus.Failed ? Color.Firebrick : i.Status == ItemStatus.ContentDiffers ? Color.DarkOrange : SystemColors.GrayText,
            };
            li.SubItems.Add(i.Attempts.ToString());
            li.SubItems.Add(i.Error ?? (i.Status == ItemStatus.NotProcessed ? "Non elaborato" : ""));
            _lvErrors.Items.Add(li);
        }
        _lvErrors.EndUpdate();
        _tabErrors.Text = _lvErrors.Items.Count > 0 ? $"Errori ({_lvErrors.Items.Count})" : "Errori";
    }

    private void InvalidatePlan(bool dropSession = false)
    {
        if (_busy) return;
        if (dropSession) { _session = null; _manualActions.Clear(); }
        if (_plan is null) return;
        _plan = null;
        _btnSync.Enabled = false;
        _lblPhase.ForeColor = SystemColors.ControlText;
        _lblPhase.Text = "Impostazioni cambiate: premi ① Confronta.";
    }

    private void SetBusy(bool busy, string? phase)
    {
        _busy = busy;
        foreach (var c in new Control[] { _btnCompare, _btnRefresh, _btnBrowseRemote, _btnBrowseLocal, _txtRemote, _txtLocal, _cmbDevice,
                     _chkSizeOnly, _chkDst, _chkOverwrite, _chkVerify, _numParallel, _numStall, _numRetries, _btnExcludes, _btnAdb, _chkDiagnostic, _cmbAlign })
            c.Enabled = !busy;
        _btnCancel.Enabled = busy && _cts is not null && !_cts.IsCancellationRequested;
        _btnSync.Enabled = !busy && _plan is { } p && p.HasWork;
        if (phase is not null)
        {
            _lblPhase.ForeColor = SystemColors.ControlText;
            _lblPhase.Text = phase;
        }
        if (busy) _lblStatusMsg.Text = "";
        else
        {
            if (_lblStatusMsg.Text.StartsWith("Annullamento", StringComparison.Ordinal)) _lblStatusMsg.Text = "";
            _lvActive.Items.Clear();
        }
        UseWaitCursor = false;
    }

    // =====================================================================  VISTA FILE

    private void UpdateFilterCounts()
    {
        foreach (var (cat, cb) in _filterChecks)
            cb.Text = $"{cb.Tag} ({Format.Number(_plan?.Count(cat) ?? 0)})";
    }

    private void RefreshView()
    {
        if (_plan is null) { _view = new(); _lvFiles.VirtualListSize = 0; return; }
        var cats = _filterChecks.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToHashSet();
        IEnumerable<SyncItem> q = _plan.Items.Where(i => cats.Contains(i.Category));
        if (_chkOnlyErrors.Checked) q = _plan.Items.Where(i => i.Status is ItemStatus.Failed or ItemStatus.NotProcessed || i.Category is ItemCategory.Collision or ItemCategory.Blocked);
        Func<SyncItem, object> key = _sortColumn switch
        {
            0 => i => StatusText(i),
            1 => i => i.Category,
            3 => i => i.Remote?.Size ?? -1,
            4 => i => i.Remote?.MtimeUnix ?? -1,
            5 => i => i.Local?.Size ?? -1,
            6 => i => i.Local?.MtimeUnix ?? -1,
            7 => i => i.Error ?? "",
            _ => i => i.RelativePath,
        };
        _view = (_sortDescending ? q.OrderByDescending(key) : q.OrderBy(key)).ToList();
        _lvFiles.VirtualListSize = _view.Count;
        _lvFiles.Invalidate();
    }

    private ListViewItem BuildListItem(int index)
    {
        if (index < 0 || index >= _view.Count) return new ListViewItem("");
        var i = _view[index];
        var li = new ListViewItem(StatusText(i)) { UseItemStyleForSubItems = true, ForeColor = CategoryColor(i.Category) };
        if (i.Status == ItemStatus.Failed) li.ForeColor = Color.Firebrick;
        else if (i.Status == ItemStatus.Done) li.ForeColor = Color.DarkGreen;
        li.SubItems.Add(CategoryText(i.Category) + (i.Category == ItemCategory.Excluded && i.Remote is null ? " (solo PC)" : ""));
        li.SubItems.Add(i.RelativePath);
        li.SubItems.Add(i.Remote is null ? "" : Format.Bytes(i.Remote.Size));
        li.SubItems.Add(Format.UnixTime(i.Remote?.MtimeUnix));
        li.SubItems.Add(i.Local is null ? "" : Format.Bytes(i.Local.Size));
        li.SubItems.Add(Format.UnixTime(i.Local?.MtimeUnix));
        string note = i.Error ?? (i.Category == ItemCategory.Excluded ? "Escluso dalla regola: " + i.ExcludedBy
            : i.NameWasSanitized ? "Salvato come: " + i.LocalRelativePath : "");
        li.SubItems.Add(note);
        return li;
    }

    private static string StatusText(SyncItem i) => i.Action switch
    {
        ItemAction.Skip => i.Category is ItemCategory.Identical or ItemCategory.OnlyOnPc ? "—"
            : i.Category == ItemCategory.Excluded ? "Escluso" : "Non copiare",
        ItemAction.FixTimes => i.Status switch
        {
            ItemStatus.Done => "✔ Date allineate",
            ItemStatus.Failed => "✖ Date",
            _ => "Allinea date",
        },
        ItemAction.VerifyAndFixTimes => i.Status switch
        {
            ItemStatus.Done => "✔ Verificato, date allineate",
            ItemStatus.ContentDiffers => "≠ Contenuto diverso",
            ItemStatus.Failed => "✖ Verifica",
            _ => "Verifica e allinea",
        },
        _ => CopyStatusText(i),
    };

    private static string CopyStatusText(SyncItem i) => i.Status switch
        {
            ItemStatus.Pending => "Da copiare",
            ItemStatus.Copying => "Copia...",
            ItemStatus.Retrying => "Ritento...",
            ItemStatus.Done => "✔ Copiato",
            ItemStatus.Failed => "✖ Errore",
            ItemStatus.NotProcessed => "Non elaborato",
            ItemStatus.Requeued => "In coda (stallo)",
            _ => "",
    };

    private static string CategoryText(ItemCategory c) => c switch
    {
        ItemCategory.New => "Nuovo",
        ItemCategory.Changed => "Modificato",
        ItemCategory.Conflict => "Conflitto",
        ItemCategory.DateOnly => "Solo data diversa",
        ItemCategory.Identical => "Identico",
        ItemCategory.OnlyOnPc => "Solo sul PC",
        ItemCategory.Collision => "Collisione nome",
        ItemCategory.Blocked => "Non copiabile",
        ItemCategory.Excluded => "Escluso",
        _ => c.ToString(),
    };

    private static Color CategoryColor(ItemCategory c) => c switch
    {
        ItemCategory.New => Color.FromArgb(0, 110, 0),
        ItemCategory.Changed => Color.FromArgb(0, 70, 170),
        ItemCategory.Conflict => Color.FromArgb(190, 90, 0),
        ItemCategory.DateOnly => Color.FromArgb(140, 110, 40),
        ItemCategory.Identical => Color.Gray,
        ItemCategory.OnlyOnPc => Color.FromArgb(120, 60, 150),
        ItemCategory.Collision => Color.Firebrick,
        ItemCategory.Blocked => Color.Firebrick,
        ItemCategory.Excluded => Color.FromArgb(110, 110, 130),
        _ => SystemColors.ControlText,
    };

    private IEnumerable<SyncItem> SelectedItems() =>
        _lvFiles.SelectedIndices.Cast<int>().Where(i => i < _view.Count).Select(i => _view[i]);

    private void SetActionOnSelection(ItemAction action)
    {
        if (_busy || _plan is null) return;
        int changed = 0;
        foreach (var i in SelectedItems())
        {
            if (i.Remote is null || i.Category is ItemCategory.Collision or ItemCategory.Blocked or ItemCategory.Excluded) continue; // non copiabili / esclusi dalle regole
            if (i.Action != action) { i.Action = action; changed++; }
            _manualActions[i.RelativePath] = action;
        }
        if (changed == 0) return;
        _lvFiles.Invalidate();
        _btnSync.Enabled = _plan.HasWork;
        _lblPhase.Text = $"Da fare: {_plan.WorkSummary}.";
    }

    private void OpenSelectedInExplorer()
    {
        var item = SelectedItems().FirstOrDefault();
        if (item is null || _plan is null) return;
        string path = PathMapper.ToLocalFullPath(_plan.LocalRoot, item.LocalRelativePath);
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else OpenFolder(Path.GetDirectoryName(path)!);
        }
        catch { }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            while (!Directory.Exists(folder) && Path.GetDirectoryName(folder) is { } parent) folder = parent;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // =====================================================================  AGGIORNAMENTO PERIODICO

    /// <summary>Altezza minima del riquadro Stato per vedere tutti i trasferimenti paralleli senza scorrere.</summary>
    private int DesiredStatusHeight()
    {
        int rows = (int)_numParallel.Value;
        int rowH = _lvActive.Font.Height + S(6);
        return S(150) + S(26) + rows * rowH;
    }

    private void InitSplitter()
    {
        _split.Panel1MinSize = S(150);
        _split.Panel2MinSize = S(160);
        int want = _settings.SplitterDistance > 0 ? _settings.SplitterDistance : DesiredStatusHeight();
        SetStatusHeight(Math.Max(want, DesiredStatusHeight()));
    }

    private void EnsureStatusHeight()
    {
        if (!_split.IsHandleCreated) return;
        int current = _split.Height - _split.SplitterDistance;
        if (current < DesiredStatusHeight()) SetStatusHeight(DesiredStatusHeight());
    }

    private void SetStatusHeight(int h)
    {
        try
        {
            int dist = _split.Height - h;
            dist = Math.Clamp(dist, _split.Panel1MinSize, Math.Max(_split.Panel1MinSize, _split.Height - _split.Panel2MinSize - _split.SplitterWidth));
            _split.SplitterDistance = dist;
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Azzera il riquadro Stato e la scheda Errori (nuovo confronto o nuova copia).</summary>
    private void ResetStatusPanel()
    {
        _engine = null;
        _pbTotal.Style = ProgressBarStyle.Continuous;
        _pbTotal.Value = 0;
        _lblTotal.Text = "";
        _lblCounts.Text = "";
        _lblCounts.ForeColor = SystemColors.ControlText;
        _lblStatusMsg.Text = "";
        _lblPhase.ForeColor = SystemColors.ControlText;
        _lvActive.Items.Clear();
        _lvErrors.Items.Clear();
        _tabErrors.Text = "Errori";
    }

    private void UiTick()
    {
        DrainLog();

        if (_busy && _compareWatch.IsRunning)
        {
            _lblPhase.Text = (_compareStatus ?? "Scansione in corso...") + $"   ·   {Format.Duration(_compareWatch.Elapsed)}";
            return;
        }

        var engine = _engine;
        if (engine is null) return;
        var p = engine.Progress;
        var (bps, eta) = p.Sample();
        long done = p.BytesProcessed;
        double frac = p.TotalBytes > 0 ? Math.Clamp((double)done / p.TotalBytes, 0, 1) : 0;
        _pbTotal.Value = (int)(frac * 1000);
        _lblPhase.Text = p.TotalBytes > 0 ? $"{p.Phase}: {frac * 100:0.0}%" : p.Phase;
        _lblStatusMsg.Text = p.StatusMessage ?? (_cts?.IsCancellationRequested == true ? "Annullamento in corso..." : "");
        var elapsed = p.Elapsed;
        _lblTotal.Text = $"{Format.Bytes(done)} di {Format.Bytes(p.TotalBytes)}   ·   {Format.Bytes((long)bps)}/s   ·   " +
                         $"trascorso {Format.Duration(elapsed)}" +
                         (p.FinishedAt is null ? $"   ·   tempo stimato rimanente {(eta is null ? "calcolo..." : Format.Duration(eta.Value))}" : "   ·   terminato");
        _lblCounts.Text = $"File: {Format.Number(p.DoneFiles)} copiati su {Format.Number(p.TotalFiles)}   ·   " +
                          (p.Stalls > 0 ? $"stalli: {p.Stalls} (in coda per nuovo tentativo: {p.RequeuedFiles})   ·   " : "") +
                          $"errori definitivi: {p.FailedFiles}   ·   in attesa di nuovo tentativo: {p.RetryingFiles}";
        _lblCounts.ForeColor = p.FailedFiles > 0 ? Color.Firebrick : SystemColors.ControlText;

        _lvActive.BeginUpdate();
        _lvActive.Items.Clear();
        foreach (var a in p.Active.Values.OrderBy(a => a.Slot))
        {
            var li = new ListViewItem(a.Slot.ToString());
            li.SubItems.Add(a.Path);
            li.SubItems.Add($"{a.Percent:0}%  ({Format.Bytes(a.Bytes)} / {Format.Bytes(a.Size)})");
            li.SubItems.Add(Format.Bytes((long)a.BytesPerSecond) + "/s");
            li.SubItems.Add(a.Attempt > 1 ? $"{a.Attempt} ⚠" : "1");
            if (a.Attempt > 1) li.ForeColor = Color.DarkOrange;
            _lvActive.Items.Add(li);
        }
        _lvActive.EndUpdate();
        _lvFiles.Invalidate();
    }

    private void DrainLog()
    {
        if (_pendingLog.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        int n = 0;
        while (n < 2000 && _pendingLog.TryDequeue(out var e))
        {
            n++;
            if (e.Level == LogLevel.Debug && !_chkDetailedLog.Checked) continue;
            sb.Append(e).Append("\r\n");
        }
        if (sb.Length == 0) return;
        if (_txtLog.TextLength > 1_500_000)
            _txtLog.Text = _txtLog.Text[^500_000..]; // il log completo resta nel file
        _txtLog.AppendText(sb.ToString());
    }

    // =====================================================================  SISTEMA

    [Flags]
    private enum ExecutionState : uint { Continuous = 0x80000000, SystemRequired = 0x00000001 }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState state);

    /// <summary>Impedisce la sospensione del PC durante la copia.</summary>
    private static void KeepAwake(bool on)
    {
        try { SetThreadExecutionState(on ? ExecutionState.Continuous | ExecutionState.SystemRequired : ExecutionState.Continuous); } catch { }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && _cts is { IsCancellationRequested: false })
        {
            if (MessageBox.Show(this, "Un'operazione è in corso. Interromperla e chiudere?\n\nI file già copiati restano integri; quelli in corso vengono scartati.",
                    "AdbSync", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cts.Cancel();
        }
        SaveControlsIntoSettings();
        _log.Dispose();
    }
}
