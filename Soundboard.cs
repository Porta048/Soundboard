using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SoundboardWindow());
    }
}

internal sealed class SoundboardWindow : Form
{
    private readonly BindingList<SoundEntry> sounds = new BindingList<SoundEntry>();
    private readonly DataGridView grid = new DataGridView();
    private readonly PcmAudioPlayer player = new PcmAudioPlayer();
    private readonly Label status = new Label();
    private readonly Label libraryCount = new Label();
    private readonly Label currentSound = new Label();
    private readonly Label elapsed = new Label();
    private readonly Label selectedCount = new Label();
    private readonly Label playCountLabel = new Label();
    private readonly ListBox categories = new ListBox();
    private readonly TableLayoutPanel content = new TableLayoutPanel();
    private readonly AudioSlider seek = new AudioSlider();
    private readonly AudioSlider volume = new AudioSlider();
    private readonly ToolTip hints = new ToolTip();
    private readonly Timer playbackTimer = new Timer();
    private readonly System.Collections.Generic.List<System.Drawing.Image> resourceImages = new System.Collections.Generic.List<System.Drawing.Image>();
    private readonly string libraryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundboard", "library.xml");
    private bool paused;
    private bool updatingSeek;
    private bool pendingPlay;
    private SoundEntry playingSound;
    private AudioRoute outputRoute;
    private string microphoneDeviceName;
    private readonly ToolStripMenuItem microphoneDevices = new ToolStripMenuItem("Dispositivo microfono virtuale");
    private readonly System.Collections.Generic.List<ToolStripMenuItem> routeCommands = new System.Collections.Generic.List<ToolStripMenuItem>();
    private Button speakerButton;
    private Button microphoneButton;
    private bool initialized;
    private long playbackRequest;
    private bool audioSettingsDirty;
    private DateTime saveAudioAfter;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public SoundboardWindow()
    {
        Text = "Soundboard";
        ClientSize = new System.Drawing.Size(758, 506);
        MinimumSize = new System.Drawing.Size(680, 420);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new System.Drawing.Font("Segoe UI", 9);
        BackColor = System.Drawing.Color.FromArgb(32, 32, 32);
        ForeColor = System.Drawing.Color.FromArgb(205, 205, 205);
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream("Soundboard.main.ico"))
        using (var icon = new System.Drawing.Icon(stream, 32, 32))
            Icon = (System.Drawing.Icon)icon.Clone();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0), Padding = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var menu = new MenuStrip { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0), BackColor = BackColor, ForeColor = ForeColor, Renderer = new DarkStripRenderer(), Font = Font };
        var fileMenu = new ToolStripMenuItem("File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Aggiungi suoni...", null, AddSounds) { ShortcutKeys = Keys.Control | Keys.O });
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Importa lista Soundpad...", null, ImportSoundpadList));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Esci", null, CloseWindow));
        var editMenu = new ToolStripMenuItem("Modifica");
        editMenu.DropDownItems.Add(new ToolStripMenuItem("Rimuovi suono", null, RemoveSelected) { ShortcutKeys = Keys.Delete });
        editMenu.DropDownItems.Add(new ToolStripMenuItem("Individua file...", null, LocateSoundFile));
        editMenu.DropDownItems.Add(new ToolStripMenuItem("Proprieta del suono...", null, ShowSoundProperties));
        var playMenu = new ToolStripMenuItem("Riproduci");
        playMenu.DropDownItems.Add(new ToolStripMenuItem("Riproduci", null, PlaySelected) { ShortcutKeys = Keys.Control | Keys.P });
        playMenu.DropDownItems.Add(new ToolStripMenuItem("Pausa / Riprendi", null, PausePlayback));
        playMenu.DropDownItems.Add(new ToolStripMenuItem("Arresta", null, StopPlayback));
        playMenu.DropDownItems.Add(new ToolStripSeparator());
        playMenu.DropDownItems.Add(new ToolStripMenuItem("Precedente", null, MoveSoundSelection) { Tag = -1 });
        playMenu.DropDownItems.Add(new ToolStripMenuItem("Successivo", null, MoveSoundSelection) { Tag = 1 });
        playMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var route in new[] { AudioRoute.Speakers, AudioRoute.Microphone, AudioRoute.Both })
        {
            var text = route == AudioRoute.Speakers ? "Riproduci file solo sugli altoparlanti" : route == AudioRoute.Microphone ? "Riproduci file solo nel microfono" : "Riproduci file su altoparlanti e microfono";
            var command = new ToolStripMenuItem(text, null, PlayWithRoute) { Tag = route };
            routeCommands.Add(command);
            playMenu.DropDownItems.Add(command);
        }
        playMenu.DropDownItems.Add(new ToolStripSeparator());
        playMenu.DropDownItems.Add(microphoneDevices);
        playMenu.DropDownItems.Add(new ToolStripMenuItem("Collegamento del microfono virtuale...", null, ShowMicrophoneHelp));
        microphoneDevices.DropDownOpening += RefreshMicrophoneDevices;
        var windowMenu = new ToolStripMenuItem("Finestra");
        windowMenu.DropDownItems.Add(new ToolStripMenuItem("Categorie", null, ToggleCategories) { Checked = true, CheckOnClick = true });
        var helpMenu = new ToolStripMenuItem("Guida");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("Informazioni", null, ShowInformation));
        menu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, playMenu, windowMenu, helpMenu });
        MainMenuStrip = menu;

        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 216));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var transport = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0), WrapContents = false };
        var play = CreateIconButton("play", "Riproduci", PlaySelected);
        speakerButton = CreateIconButton("headphones", "Riproduci solo sugli altoparlanti", PlayWithRoute);
        speakerButton.Tag = AudioRoute.Speakers;
        microphoneButton = CreateIconButton("microphone", "Riproduci solo nel microfono virtuale", PlayWithRoute);
        microphoneButton.Tag = AudioRoute.Microphone;
        var pause = CreateIconButton("pause", "Pausa / Riprendi", PausePlayback);
        var stop = CreateIconButton("stop", "Arresta", StopPlayback);
        var previous = CreateIconButton("previous", "Suono precedente", MoveSoundSelection);
        previous.Tag = -1;
        var next = CreateIconButton("next", "Suono successivo", MoveSoundSelection);
        next.Tag = 1;
        transport.Controls.AddRange(new Control[] { play, speakerButton, microphoneButton, pause, stop, previous, next });
        var timeline = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timeline.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        elapsed.Text = "0:00";
        elapsed.Dock = DockStyle.Fill;
        elapsed.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        seek.Dock = DockStyle.Fill;
        seek.Margin = new Padding(0, 0, 4, 0);
        seek.AccessibleName = "Posizione di riproduzione";
        timeline.Controls.Add(elapsed, 0, 0);
        timeline.Controls.Add(seek, 1, 0);
        var volumePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), Padding = new Padding(4, 0, 0, 0), BackColor = BackColor };
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        volumePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var volumeIcon = CreateIconButton("headphones", "Volume", null);
        volumeIcon.TabStop = false;
        volume.Dock = DockStyle.Fill;
        volume.Margin = new Padding(0);
        volume.ShowThumb = true;
        volume.Value = 800;
        volume.AccessibleName = "Volume";
        volumePanel.Controls.Add(volumeIcon, 0, 0);
        volumePanel.Controls.Add(volume, 1, 0);
        toolbar.Controls.Add(transport, 0, 0);
        toolbar.Controls.Add(timeline, 1, 0);
        toolbar.Controls.Add(volumePanel, 2, 0);

        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0);
        content.ColumnCount = 2;
        content.RowCount = 1;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 182));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 3, 0), BackColor = System.Drawing.Color.FromArgb(50, 50, 50) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var categoryHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        categoryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        categoryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        categoryHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        categoryHeader.Controls.Add(new Label { Text = "Categoria", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, Margin = new Padding(0) }, 0, 0);
        categoryHeader.Controls.Add(new Label { Text = "#", Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleCenter, Margin = new Padding(0) }, 1, 0);
        categories.Dock = DockStyle.Fill;
        categories.Margin = new Padding(0);
        categories.BorderStyle = BorderStyle.None;
        categories.BackColor = sidebar.BackColor;
        categories.ForeColor = ForeColor;
        categories.DrawMode = DrawMode.OwnerDrawFixed;
        categories.ItemHeight = 30;
        categories.IntegralHeight = false;
        categories.Items.Add("I miei suoni");
        categories.SelectedIndex = 0;
        sidebar.Controls.Add(categoryHeader, 0, 0);
        sidebar.Controls.Add(categories, 0, 1);

        grid.Dock = DockStyle.Fill;
        grid.Margin = new Padding(0);
        grid.BackgroundColor = System.Drawing.Color.FromArgb(40, 40, 40);
        grid.GridColor = System.Drawing.Color.FromArgb(75, 75, 75);
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeight = 24;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = System.Drawing.Color.FromArgb(40, 40, 40), ForeColor = ForeColor, Font = Font, Padding = new Padding(4, 0, 0, 0), SelectionBackColor = System.Drawing.Color.FromArgb(40, 40, 40) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = grid.BackgroundColor, ForeColor = ForeColor, SelectionBackColor = System.Drawing.Color.FromArgb(50, 50, 50), SelectionForeColor = ForeColor, Padding = new Padding(4, 0, 0, 0) };
        grid.RowTemplate.Height = 23;
        grid.AllowUserToResizeRows = false;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.RowHeadersVisible = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Index", HeaderText = "Ind...", Width = 44, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Etichetta", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Duration", HeaderText = "Dur...", Width = 44 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Shortcut", HeaderText = "Tasto di scelta...", Width = 116 });
        grid.DataSource = sounds;

        var library = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        library.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        library.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        library.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        library.Controls.Add(grid, 0, 0);
        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = new Padding(0), BackColor = System.Drawing.Color.FromArgb(50, 50, 50), Padding = new Padding(3, 7, 3, 7) };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var home = AudioArtwork.Create("home", 52);
        resourceImages.Add(home);
        var homeImage = new PictureBox { Image = home, Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0) };
        summary.Controls.Add(homeImage, 0, 0);
        summary.SetRowSpan(homeImage, 2);
        currentSound.Text = "I miei suoni";
        currentSound.Dock = DockStyle.Fill;
        currentSound.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        summary.Controls.Add(currentSound, 1, 0);
        libraryCount.Dock = DockStyle.Fill;
        libraryCount.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        selectedCount.Dock = DockStyle.Fill;
        selectedCount.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        playCountLabel.Dock = DockStyle.Fill;
        playCountLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        summary.Controls.Add(libraryCount, 2, 0);
        summary.Controls.Add(selectedCount, 2, 1);
        summary.Controls.Add(playCountLabel, 3, 0);
        library.Controls.Add(summary, 0, 1);
        content.Controls.Add(sidebar, 0, 0);
        content.Controls.Add(library, 1, 0);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var footerPlay = CreateIconButton("play", "Riproduci", PlaySelected);
        footerPlay.Size = new System.Drawing.Size(24, 24);
        status.Dock = DockStyle.Fill;
        status.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        status.Text = "";
        footer.Controls.Add(footerPlay, 0, 0);
        footer.Controls.Add(status, 1, 0);
        layout.Controls.Add(menu, 0, 0);
        layout.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(content, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        categories.DrawItem += DrawCategory;
        grid.CellDoubleClick += PlaySelected;
        grid.CellFormatting += FormatSoundIndex;
        grid.SelectionChanged += UpdateSelection;
        grid.CellToolTipTextNeeded += ShowSoundPath;
        volume.ValueChanged += ChangeVolume;
        seek.ValueChanged += SeekPlayback;
        player.PlaybackStopped += PlaybackEnded;
        playbackTimer.Interval = 250;
        playbackTimer.Tick += UpdatePlaybackPosition;
        playbackTimer.Start();
        player.Volume = 0.8;
        LoadLibrary();
        LoadAudioSettings();
        RefreshHotkeys();
        UpdateLibraryAppearance();
        ChangeVolume(this, EventArgs.Empty);
        UpdateOutputRoute();
        RefreshMicrophoneDevices(this, EventArgs.Empty);
        initialized = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var darkMode = 1;
        DwmSetWindowAttribute(Handle, 20, ref darkMode, sizeof(int));
        var captionColor = 0x202020;
        DwmSetWindowAttribute(Handle, 35, ref captionColor, sizeof(int));
        RefreshHotkeys();
    }

    private Button CreateIconButton(string glyph, string description, EventHandler handler)
    {
        var image = AudioArtwork.Create(glyph, 22);
        resourceImages.Add(image);
        var button = new Button { Image = image, Size = new System.Drawing.Size(28, 28), Margin = new Padding(0), Padding = new Padding(0), FlatStyle = FlatStyle.Flat, BackColor = BackColor, UseVisualStyleBackColor = false, AccessibleName = description, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = System.Drawing.Color.FromArgb(55, 55, 55);
        button.FlatAppearance.MouseDownBackColor = System.Drawing.Color.FromArgb(68, 68, 68);
        hints.SetToolTip(button, description);
        if (handler != null) button.Click += handler;
        return button;
    }

    private void DrawCategory(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        using (var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(70, 70, 70)))
            e.Graphics.FillRectangle(background, e.Bounds);
        using (var home = AudioArtwork.Create("home", 26))
            e.Graphics.DrawImage(home, 18, e.Bounds.Top + 1, 26, 26);
        TextRenderer.DrawText(e.Graphics, "I miei suoni", Font, new System.Drawing.Rectangle(46, e.Bounds.Top, e.Bounds.Width - 72, e.Bounds.Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, sounds.Count.ToString(), Font, new System.Drawing.Rectangle(e.Bounds.Width - 28, e.Bounds.Top, 26, e.Bounds.Height), ForeColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    private void FormatSoundIndex(object sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
        e.Value = (sounds.IndexOf((SoundEntry)grid.Rows[e.RowIndex].DataBoundItem) + 1).ToString();
        e.FormattingApplied = true;
    }

    private void UpdateSelection(object sender, EventArgs e)
    {
        selectedCount.Text = "Selezionato:    " + grid.SelectedRows.Count;
    }

    private void CloseWindow(object sender, EventArgs e)
    {
        Close();
    }

    private void ToggleCategories(object sender, EventArgs e)
    {
        var item = (ToolStripMenuItem)sender;
        content.Controls[0].Visible = item.Checked;
        content.ColumnStyles[0].Width = item.Checked ? 182 : 0;
    }

    private void ShowInformation(object sender, EventArgs e)
    {
        MessageBox.Show(this, "Soundboard\n\nLibreria audio con riproduzione e scorciatoie globali.", "Informazioni", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void MoveSoundSelection(object sender, EventArgs e)
    {
        if (sounds.Count == 0) return;
        var control = sender as Control;
        var menuItem = sender as ToolStripItem;
        var direction = (int)(control != null ? control.Tag : menuItem.Tag);
        var index = grid.CurrentRow == null ? 0 : grid.CurrentRow.Index;
        index = Math.Max(0, Math.Min(sounds.Count - 1, index + direction));
        grid.CurrentCell = grid.Rows[index].Cells[1];
        PlaySelected(this, EventArgs.Empty);
    }

    private void ChangeVolume(object sender, EventArgs e)
    {
        player.Volume = volume.Value / 1000.0;
        hints.SetToolTip(volume, "Volume: " + volume.Value / 10 + "%");
        if (initialized)
        {
            audioSettingsDirty = true;
            saveAudioAfter = DateTime.UtcNow.AddMilliseconds(400);
        }
    }

    private void PlayWithRoute(object sender, EventArgs e)
    {
        var control = sender as Control;
        var item = sender as ToolStripItem;
        outputRoute = (AudioRoute)(control != null ? control.Tag : item.Tag);
        UpdateOutputRoute();
        SaveAudioSettings();
        PlaySelected(this, EventArgs.Empty);
    }

    private void UpdateOutputRoute()
    {
        foreach (var item in routeCommands) item.Checked = (AudioRoute)item.Tag == outputRoute;
        speakerButton.BackColor = outputRoute != AudioRoute.Microphone ? System.Drawing.Color.FromArgb(55, 55, 55) : BackColor;
        microphoneButton.BackColor = outputRoute != AudioRoute.Speakers ? System.Drawing.Color.FromArgb(55, 55, 55) : BackColor;
        hints.SetToolTip(microphoneButton, "Riproduci solo nel microfono virtuale" + (string.IsNullOrEmpty(microphoneDeviceName) ? "" : ": " + microphoneDeviceName));
    }

    private void RefreshMicrophoneDevices(object sender, EventArgs e)
    {
        foreach (var item in microphoneDevices.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
        try
        {
            var devices = AudioRouting.VirtualDevices();
            foreach (var device in devices)
                microphoneDevices.DropDownItems.Add(new ToolStripMenuItem(device.Name, null, SelectMicrophoneDevice) { Tag = device.Name, Checked = device.Name == microphoneDeviceName });
            if (devices.Length == 0) microphoneDevices.DropDownItems.Add(new ToolStripMenuItem("Nessun ingresso virtuale disponibile") { Enabled = false });
        }
        catch (Exception error)
        {
            microphoneDevices.DropDownItems.Add(new ToolStripMenuItem("Dispositivi non disponibili") { Enabled = false });
            status.Text = "Dispositivi audio non disponibili: " + error.Message;
        }
    }

    private void SelectMicrophoneDevice(object sender, EventArgs e)
    {
        if (outputRoute != AudioRoute.Speakers) StopPlayback(this, EventArgs.Empty);
        microphoneDeviceName = (string)((ToolStripMenuItem)sender).Tag;
        UpdateOutputRoute();
        SaveAudioSettings();
        status.Text = "Microfono virtuale: " + microphoneDeviceName;
    }

    private void ShowMicrophoneHelp(object sender, EventArgs e)
    {
        var device = microphoneDeviceName ?? "";
        var bus = device.IndexOf("AUX", StringComparison.OrdinalIgnoreCase) >= 0 ? "B2" : device.IndexOf("VAIO3", StringComparison.OrdinalIgnoreCase) >= 0 ? "B3" : "B1";
        var captureName = "Voicemeeter Out " + bus;
        try { captureName = NativeAudio.RecordingDevices().FirstOrDefault(name => name.StartsWith(captureName + " ", StringComparison.OrdinalIgnoreCase)) ?? captureName; }
        catch (IOException) { }
        var text = device.IndexOf("Voicemeeter", StringComparison.OrdinalIgnoreCase) >= 0
            ? "1. Apri Voicemeeter e attiva " + bus + " sull'ingresso scelto.\n2. Per non ascoltare il file localmente, disattiva A1 e gli altri bus A su quell'ingresso.\n3. Nell'app di chiamata o registrazione scegli " + captureName + " come microfono.\n4. Se vuoi inviare anche la tua voce, instrada il microfono fisico sullo stesso bus."
            : "Seleziona l'ingresso del cavo virtuale dal menu Riproduci. Nell'app di chiamata o registrazione scegli la corrispondente uscita del cavo come microfono.";
        MessageBox.Show(this, "Dispositivo: " + (string.IsNullOrEmpty(device) ? "Da selezionare" : device) + "\n\n" + text + "\n\nLa modalita solo microfono invia il file esclusivamente all'ingresso virtuale scelto.", "Collegamento del microfono virtuale", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadAudioSettings()
    {
        try
        {
            microphoneDeviceName = AudioRouting.VirtualDevices().Select(device => device.Name).FirstOrDefault();
            var path = Path.Combine(Path.GetDirectoryName(libraryPath), "settings.xml");
            if (!File.Exists(path)) return;
            var document = ReadLibraryDocument(path);
            if (document.Root == null || document.Root.Name != "audio") throw new FormatException("Formato impostazioni non valido.");
            int level;
            if (int.TryParse((string)document.Root.Attribute("volume"), out level)) volume.Value = level;
            var deviceName = (string)document.Root.Attribute("microphoneDevice");
            if (deviceName != null) microphoneDeviceName = deviceName;
            AudioRoute route;
            if (Enum.TryParse((string)document.Root.Attribute("route"), out route) && Enum.IsDefined(typeof(AudioRoute), route)) outputRoute = route;
        }
        catch (Exception error) { status.Text = "Impossibile caricare le impostazioni audio: " + error.Message; }
    }

    private void SaveAudioSettings()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(libraryPath), "settings.xml");
            var document = new XDocument(new XElement("audio", new XAttribute("volume", volume.Value), new XAttribute("route", outputRoute), new XAttribute("microphoneDevice", microphoneDeviceName ?? "")));
            SaveDocumentAtomic(document, path);
            audioSettingsDirty = false;
        }
        catch (Exception error) { status.Text = "Impossibile salvare le impostazioni audio: " + error.Message; audioSettingsDirty = false; }
    }

    private void SeekPlayback(object sender, EventArgs e)
    {
        if (updatingSeek || player.Source == null || player.Duration <= TimeSpan.Zero) return;
        try { player.Position = TimeSpan.FromMilliseconds(player.Duration.TotalMilliseconds * seek.Value / 1000.0); }
        catch (Exception error) { StopPlayback(this, EventArgs.Empty); status.Text = "Uscita audio non disponibile: " + error.Message; }
    }

    private void UpdatePlaybackPosition(object sender, EventArgs e)
    {
        if (audioSettingsDirty && DateTime.UtcNow >= saveAudioAfter) SaveAudioSettings();
        if (player.Source == null || player.Duration <= TimeSpan.Zero) return;
        try
        {
            var position = player.Position;
            elapsed.Text = (int)position.TotalMinutes + ":" + position.Seconds.ToString("D2");
            if (seek.Capture) return;
            updatingSeek = true;
            seek.Value = (int)(position.TotalMilliseconds * 1000 / Math.Max(1, player.Duration.TotalMilliseconds));
        }
        catch (Exception error) { StopPlayback(this, EventArgs.Empty); status.Text = "Uscita audio non disponibile: " + error.Message; }
        finally { updatingSeek = false; }
    }

    private void PlaybackOpened(object sender, EventArgs e)
    {
        if (playingSound == null || !sounds.Contains(playingSound) || player.Duration <= TimeSpan.Zero) return;
        var duration = player.Duration;
        playingSound.Duration = (int)duration.TotalMinutes + ":" + duration.Seconds.ToString("D2");
        if (pendingPlay)
        {
            if (playingSound.PlayCount < int.MaxValue) playingSound.PlayCount += 1;
            playingSound.LastPlayedOn = DateTime.Now.ToString("yyyy-MM-dd");
            pendingPlay = false;
        }
        sounds.ResetItem(sounds.IndexOf(playingSound));
        UpdateLibraryAppearance();
        SaveLibrary();
    }

    private void UpdateLibraryAppearance()
    {
        libraryCount.Text = "Suoni:             " + sounds.Count;
        playCountLabel.Text = "Conteggio riproduzioni:  " + sounds.Sum(sound => (long)sound.PlayCount);
        categories.Invalidate();
        UpdateSelection(this, EventArgs.Empty);
    }

    private void AddSounds(object sender, EventArgs e)
    {
        using (var dialog = new OpenFileDialog { Multiselect = true, Filter = "Audio e video (*.wav;*.mp3;*.wma;*.m4a;*.mp4;*.aac;*.flac;*.ogg;*.opus;*.mkv;*.webm)|*.wav;*.mp3;*.wma;*.m4a;*.mp4;*.aac;*.flac;*.ogg;*.opus;*.mkv;*.webm|Tutti i file (*.*)|*.*" })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            foreach (var path in dialog.FileNames)
            {
                if (!sounds.Any(sound => string.Equals(sound.Path, path, StringComparison.OrdinalIgnoreCase)))
                    sounds.Add(new SoundEntry { Path = path, AddedOn = DateTime.Now.ToString("yyyy-MM-dd") });
            }
        }
        RefreshHotkeys();
        SaveLibrary();
    }

    private void PlaySelected(object sender, EventArgs e)
    {
        var selected = grid.CurrentRow == null ? null : grid.CurrentRow.DataBoundItem as SoundEntry;
        if (selected != null) PlaySound(selected);
    }

    private void ImportSoundpadList(object sender, EventArgs e)
    {
        using (var dialog = new OpenFileDialog { Filter = "Lista Soundpad (*.spl)|*.spl", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Leppsoft") })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { ImportSoundpadFile(dialog.FileName); }
            catch (Exception error) { status.Text = "Impossibile importare la lista: " + error.Message; }
        }
    }

    private int ImportSoundpadFile(string path)
    {
        var document = ReadLibraryDocument(path);
        if (document.Root == null || document.Root.Name != "Soundlist") throw new FormatException("Il file non contiene una lista Soundpad.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var statsPath = Path.Combine(directory, "stats.spl");
        var stats = new XElement("Stats");
        var statsUnavailable = false;
        if (File.Exists(statsPath))
        {
            try
            {
                var statsDocument = ReadLibraryDocument(statsPath);
                if (statsDocument.Root == null || statsDocument.Root.Name != "Stats") throw new FormatException("Formato statistiche non valido.");
                stats = statsDocument.Root;
            }
            catch (Exception) { statsUnavailable = true; }
        }
        var counts = stats.Elements("Sound").Where(element => !string.IsNullOrEmpty((string)element.Attribute("hash")))
            .GroupBy(element => (string)element.Attribute("hash")).ToDictionary(group => group.Key, group => group.Last());
        var imported = new System.Collections.Generic.List<SoundEntry>();
        foreach (var element in document.Root.Elements("Sound"))
        {
            var soundPath = ResolveSoundPath((string)element.Attribute("url"), directory);
            if (soundPath == null) continue;
            if (sounds.Any(sound => string.Equals(sound.Path, soundPath, StringComparison.OrdinalIgnoreCase)) || imported.Any(sound => string.Equals(sound.Path, soundPath, StringComparison.OrdinalIgnoreCase))) continue;
            var hash = (string)element.Attribute("hash");
            XElement statistics;
            counts.TryGetValue(hash ?? "", out statistics);
            int count;
            int.TryParse(statistics == null ? null : (string)statistics.Attribute("playCount"), out count);
            imported.Add(new SoundEntry {
                Path = soundPath,
                Title = (string)element.Attribute("title"),
                Duration = (string)element.Attribute("duration"),
                SourceHash = hash,
                AddedOn = (string)element.Attribute("addedOn"),
                PlayCount = Math.Max(0, count),
                LastPlayedOn = statistics == null ? null : (string)statistics.Attribute("lastPlayedOn")
            });
        }
        foreach (var sound in imported) sounds.Add(sound);
        RefreshHotkeys();
        UpdateLibraryAppearance();
        if (SaveLibrary())
        {
            status.Text = "Importati " + imported.Count + " suoni. File mancanti: " + imported.Count(sound => !File.Exists(sound.Path)) + ".";
            if (statsUnavailable) status.Text += " Statistiche non disponibili.";
        }
        return imported.Count;
    }

    private static string ResolveSoundPath(string value, string directory)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        Uri uri;
        if (Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            if (!uri.IsFile || uri.IsUnc) return null;
            value = uri.LocalPath;
        }
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(directory, value));
    }

    private void LocateSoundFile(object sender, EventArgs e)
    {
        var selected = grid.CurrentRow == null ? null : grid.CurrentRow.DataBoundItem as SoundEntry;
        if (selected == null) return;
        using (var dialog = new OpenFileDialog { Filter = "Audio e video|*.wav;*.mp3;*.wma;*.m4a;*.mp4;*.aac;*.flac;*.ogg;*.opus;*.mkv;*.webm|Tutti i file (*.*)|*.*", FileName = Path.GetFileName(selected.Path) })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (sounds.Any(sound => sound != selected && string.Equals(sound.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                status.Text = "Il file e gia presente nella libreria.";
                return;
            }
            if (playingSound == selected) StopPlayback(this, EventArgs.Empty);
            selected.Path = dialog.FileName;
            selected.Duration = null;
        }
        sounds.ResetItem(sounds.IndexOf(selected));
        if (SaveLibrary()) status.Text = "File ricollegato: " + selected.Name;
    }

    private void ShowSoundProperties(object sender, EventArgs e)
    {
        var selected = grid.CurrentRow == null ? null : grid.CurrentRow.DataBoundItem as SoundEntry;
        if (selected == null) return;
        MessageBox.Show(this, selected.Name + "\n\nFile: " + selected.Path + "\nDurata: " + selected.Duration + "\nRiproduzioni: " + selected.PlayCount + "\nStato: " + (File.Exists(selected.Path) ? "Disponibile" : "File non trovato"), "Proprieta del suono", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowSoundPath(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var sound = grid.Rows[e.RowIndex].DataBoundItem as SoundEntry;
        if (sound != null) e.ToolTipText = sound.Path + (File.Exists(sound.Path) ? "" : "\nFile non trovato. Usa Modifica > Individua file.");
    }

    private async void PlaySound(SoundEntry sound)
    {
        var request = ++playbackRequest;
        player.Close();
        playingSound = null;
        pendingPlay = false;
        paused = false;
        elapsed.Text = "0:00";
        seek.Value = 0;
        if (!File.Exists(sound.Path))
        {
            status.Text = "File non trovato. Usa Modifica > Individua file: " + sound.Path;
            hints.SetToolTip(status, status.Text);
            return;
        }
        playingSound = sound;
        pendingPlay = true;
        status.Text = "Caricamento: " + sound.Name;
        try
        {
            var devices = AudioRouting.Resolve(outputRoute, microphoneDeviceName);
            player.Volume = volume.Value / 1000.0;
            await player.OpenAsync(sound.Path, devices);
            if (request != playbackRequest || IsDisposed || Disposing) return;
            player.Play();
            PlaybackOpened(this, EventArgs.Empty);
            status.Text = "In riproduzione: " + sound.Name + (outputRoute == AudioRoute.Microphone ? " | Solo microfono virtuale" : outputRoute == AudioRoute.Both ? " | Altoparlanti e microfono" : " | Altoparlanti");
            hints.SetToolTip(status, status.Text);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (request != playbackRequest || IsDisposed || Disposing) return;
            player.Close();
            pendingPlay = false;
            playingSound = null;
            status.Text = "Riproduzione non riuscita: " + error.Message;
            hints.SetToolTip(status, status.Text);
        }
    }

    private void PausePlayback(object sender, EventArgs e)
    {
        if (player.Source == null) return;
        try
        {
            if (paused) player.Play();
            else player.Pause();
            paused = !paused;
            status.Text = paused ? "In pausa" : "Riproduzione ripresa";
        }
        catch (Exception error) { StopPlayback(this, EventArgs.Empty); status.Text = "Uscita audio non disponibile: " + error.Message; }
    }

    private void StopPlayback(object sender, EventArgs e)
    {
        playbackRequest += 1;
        player.Close();
        playingSound = null;
        pendingPlay = false;
        paused = false;
        elapsed.Text = "0:00";
        seek.Value = 0;
        status.Text = "Riproduzione arrestata";
    }

    private void RemoveSelected(object sender, EventArgs e)
    {
        var selected = grid.CurrentRow == null ? null : grid.CurrentRow.DataBoundItem as SoundEntry;
        if (selected == null) return;
        sounds.Remove(selected);
        RefreshHotkeys();
        SaveLibrary();
    }

    private void RefreshHotkeys()
    {
        if (!IsHandleCreated) return;
        foreach (var id in Enumerable.Range(1, 12)) UnregisterHotKey(Handle, id);
        foreach (var index in Enumerable.Range(0, sounds.Count))
        {
            var sound = sounds[index];
            sound.Shortcut = index < 9
                ? (RegisterHotKey(Handle, index + 1, 0x4003, (uint)(Keys.D1 + index)) ? "Ctrl+Alt+" + (index + 1) : "Gia in uso")
                : "";
        }
        sounds.ResetBindings();
        UpdateLibraryAppearance();
        if (!RegisterHotKey(Handle, 12, 0x4003, (uint)Keys.F12))
            status.Text = "Scorciatoia stop Ctrl+Alt+F12 gia in uso.";
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312)
        {
            var id = message.WParam.ToInt32();
            if (id == 12) StopPlayback(this, EventArgs.Empty);
            else if (id >= 1 && id <= Math.Min(sounds.Count, 9)) PlaySound(sounds[id - 1]);
        }
        base.WndProc(ref message);
    }

    private void LoadLibrary()
    {
        if (!File.Exists(libraryPath)) return;
        try
        {
            var document = ReadLibraryDocument(libraryPath);
            if (document.Root == null || document.Root.Name != "library") throw new FormatException("Formato libreria non valido.");
            foreach (var element in document.Root.Elements("sound"))
            {
                var path = (string)element.Attribute("path");
                if (!string.IsNullOrWhiteSpace(path) && System.IO.Path.IsPathRooted(path))
                {
                    int count;
                    int.TryParse((string)element.Attribute("playCount"), out count);
                    sounds.Add(new SoundEntry {
                        Path = path,
                        Title = (string)element.Attribute("title"),
                        Duration = (string)element.Attribute("duration"),
                        SourceHash = (string)element.Attribute("sourceHash"),
                        PlayCount = Math.Max(0, count),
                        AddedOn = (string)element.Attribute("addedOn"),
                        LastPlayedOn = (string)element.Attribute("lastPlayedOn")
                    });
                }
            }
        }
        catch (Exception error)
        {
            status.Text = "Impossibile caricare la libreria: " + error.Message;
        }
    }

    private bool SaveLibrary()
    {
        try
        {
            var document = new XDocument(new XElement("library", sounds.Select(sound => new XElement("sound",
                new XAttribute("path", sound.Path),
                new XAttribute("title", sound.Name),
                string.IsNullOrEmpty(sound.Duration) ? null : new XAttribute("duration", sound.Duration),
                string.IsNullOrEmpty(sound.SourceHash) ? null : new XAttribute("sourceHash", sound.SourceHash),
                sound.PlayCount == 0 ? null : new XAttribute("playCount", sound.PlayCount),
                string.IsNullOrEmpty(sound.AddedOn) ? null : new XAttribute("addedOn", sound.AddedOn),
                string.IsNullOrEmpty(sound.LastPlayedOn) ? null : new XAttribute("lastPlayedOn", sound.LastPlayedOn)))));
            SaveDocumentAtomic(document, libraryPath);
            return true;
        }
        catch (Exception error)
        {
            status.Text = "Impossibile salvare la libreria: " + error.Message;
            return false;
        }
    }

    private static XDocument ReadLibraryDocument(string path)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
        using (var reader = System.Xml.XmlReader.Create(path, settings)) return XDocument.Load(reader);
    }

    private static void SaveDocumentAtomic(XDocument document, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporaryPath = path + ".tmp";
        document.Save(temporaryPath);
        if (File.Exists(path)) File.Replace(temporaryPath, path, null);
        else File.Move(temporaryPath, path);
    }

    private void PlaybackEnded(object sender, AudioPlaybackEventArgs e)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new EventHandler<AudioPlaybackEventArgs>(PlaybackEnded), sender, e); }
            catch (InvalidOperationException) { }
            return;
        }
        if (e.Session != player.Session) return;
        StopPlayback(sender, e);
        if (e.Error != null) status.Text = "Uscita audio non disponibile: " + e.Error.Message;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        playbackRequest += 1;
        if (audioSettingsDirty) SaveAudioSettings();
        playbackTimer.Stop();
        playbackTimer.Dispose();
        hints.Dispose();
        foreach (var id in Enumerable.Range(1, 12)) UnregisterHotKey(Handle, id);
        player.Dispose();
        base.OnFormClosed(e);
        foreach (var image in resourceImages) image.Dispose();
        Icon.Dispose();
    }
}

internal sealed class SoundEntry
{
    public string Path { get; set; }
    public string Title { get; set; }
    public string Name { get { return string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title; } }
    public string Shortcut { get; set; }
    public string Duration { get; set; }
    public string SourceHash { get; set; }
    public int PlayCount { get; set; }
    public string AddedOn { get; set; }
    public string LastPlayedOn { get; set; }
}
