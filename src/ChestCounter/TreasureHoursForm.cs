using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>
/// « Heures trésor » : la liste des fenêtres où la chance du token est multipliée (passées, en cours, à venir), avec
/// de quoi en ajouter (tel jour, de telle heure à telle heure, ×1,5 / ×2…), en supprimer ou revenir à la liste
/// intégrée. Les parties commencées pendant une fenêtre comptent avec son multiplicateur dans la chance du token.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TreasureHoursForm : Form
{
    private const int W = 480, Pad = 20;
    private static readonly double[] Multipliers = [1.5, 2, 2.5, 3];

    private readonly Monitor _monitor;
    private readonly List<TreasureHour> _hours;
    private readonly ListBox _list;
    private readonly DateTimePicker _day, _from, _to;
    private readonly ComboBox _multiplier;
    private readonly Label _status;

    public TreasureHoursForm(Monitor monitor)
    {
        _monitor = monitor;
        _hours = monitor.TreasureHours.Select(h => h.Copy()).ToList();

        Text = L.T("Horas tesoro", "Treasure Hours");
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = Theme.Text();
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = AppIcon.Load();

        Lbl(L.T("Horas tesoro", "Treasure Hours"), Theme.Text(12f, FontStyle.Bold), Theme.Ink, new Point(Pad, Pad), new Size(W - 2 * Pad, 24));
        Lbl(L.T("Durante estas horas Epic multiplica la probabilidad del token (×1,5 = 1 entre 91 667 por cofre). Las partidas empezadas dentro cuentan con ese multiplicador. Horas en tu hora local.",
                "During these hours Epic multiplies the token chance (×1.5 = 1 in 91,667 per chest). Matches started inside count with that multiplier. Times in your local time."),
            Theme.Text(8.5f), Theme.Muted, new Point(Pad, Pad + 28), new Size(W - 2 * Pad, 48));

        _list = new ListBox
        {
            Location = new Point(Pad, Pad + 80),
            Size = new Size(W - 2 * Pad, 170),
            BackColor = Theme.Card,
            ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.None,
            Font = Theme.Text(9f),
            IntegralHeight = false,
        };
        Controls.Add(_list);

        var y = Pad + 258;
        var delete = new PillButton { Text = L.T("Eliminar la seleccionada", "Delete selected"), Size = new Size(190, 30), Location = new Point(Pad, y), Radius = 10 };
        delete.Click += (_, _) => DeleteSelected();
        var reset = new PillButton { Text = L.T("Lista de origen", "Original list"), Size = new Size(150, 30), Location = new Point(W - Pad - 150, y), Radius = 10 };
        reset.Click += (_, _) => ResetDefaults();
        Controls.Add(delete);
        Controls.Add(reset);

        // Añadir: tal día, de tal hora a tal hora, con tal multiplicador.
        y += 44;
        Lbl(L.T("Añadir una hora tesoro", "Add a Treasure Hour"), Theme.Text(9.5f, FontStyle.Bold), Theme.Ink, new Point(Pad, y), new Size(W - 2 * Pad, 20));
        y += 26;
        Lbl(L.T("Día", "Day"), Theme.Text(8f), Theme.Muted, new Point(Pad, y), new Size(130, 16));
        Lbl(L.T("De", "From"), Theme.Text(8f), Theme.Muted, new Point(Pad + 140, y), new Size(80, 16));
        Lbl(L.T("A", "To"), Theme.Text(8f), Theme.Muted, new Point(Pad + 230, y), new Size(80, 16));
        Lbl(L.T("Bonus", "Boost"), Theme.Text(8f), Theme.Muted, new Point(Pad + 320, y), new Size(100, 16));
        y += 18;
        var today = DateTime.Today;
        _day = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = today, Location = new Point(Pad, y), Width = 130 };
        _from = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = today.AddHours(20), Location = new Point(Pad + 140, y), Width = 80 };
        _to = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = today.AddHours(22), Location = new Point(Pad + 230, y), Width = 80 };
        _multiplier = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(Pad + 320, y), Width = 70, BackColor = Theme.Panel, ForeColor = Theme.Ink, FlatStyle = FlatStyle.Flat };
        foreach (var m in Multipliers)
        {
            _multiplier.Items.Add("×" + m.ToString("0.##", L.Culture));
        }
        _multiplier.SelectedIndex = 0;
        Controls.Add(_day);
        Controls.Add(_from);
        Controls.Add(_to);
        Controls.Add(_multiplier);
        var add = new PillButton
        {
            Text = L.T("Añadir", "Add"),
            Size = new Size(W - 2 * Pad - 400, 28),
            Location = new Point(Pad + 400, y - 2),
            Radius = 10,
            BackColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            ForeColor = Theme.AccentInk,
        };
        add.Click += (_, _) => Add();
        Controls.Add(add);

        y += 38;
        _status = Lbl("", Theme.Text(8.5f), Theme.Green, new Point(Pad, y), new Size(W - 2 * Pad, 18));
        y += 26;
        var close = new PillButton { Text = L.T("Cerrar", "Close"), Size = new Size(100, 34), Location = new Point(W - Pad - 100, y), Radius = 11, DialogResult = DialogResult.Cancel };
        Controls.Add(close);
        CancelButton = close;
        ClientSize = new Size(W, y + 34 + Pad);

        Fill();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Chrome(Handle);
    }

    /// <summary>Remplit la liste, triée par date, avec l'état de chaque fenêtre.</summary>
    private void Fill(TreasureHour? select = null)
    {
        _hours.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        var now = DateTime.UtcNow;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var h in _hours)
        {
            var start = h.StartUtc.ToLocalTime();
            var end = h.EndUtc.ToLocalTime();
            var state = h.Contains(now) ? L.T("en curso", "in progress")
                : h.EndUtc <= now ? L.T("pasada", "past")
                : L.T("próxima", "upcoming");
            _list.Items.Add($"{start.ToString("ddd dd/MM", L.Culture)}   {start:HH:mm} – {end:HH:mm}   ×{h.Multiplier.ToString("0.##", L.Culture)}   ·  {state}");
        }
        _list.EndUpdate();
        if (select != null && _hours.IndexOf(select) is var i and >= 0)
        {
            _list.SelectedIndex = i;
        }
    }

    private void Add()
    {
        var day = _day.Value.Date;
        var start = day + _from.Value.TimeOfDay;
        var end = day + _to.Value.TimeOfDay;
        if (end <= start)
        {
            end = end.AddDays(1); // ex. 23:00 – 01:00 : finit le lendemain
        }
        var hour = new TreasureHour
        {
            StartUtc = DateTime.SpecifyKind(start, DateTimeKind.Local).ToUniversalTime(),
            EndUtc = DateTime.SpecifyKind(end, DateTimeKind.Local).ToUniversalTime(),
            Multiplier = Multipliers[Math.Max(0, _multiplier.SelectedIndex)],
        };
        _hours.Add(hour);
        Save();
        Fill(hour);
        _status.ForeColor = Theme.Green;
        _status.Text = L.F("Añadida: {0}", "Added: {0}", $"{start.ToString("ddd dd/MM HH:mm", L.Culture)} – {end:HH:mm}");
    }

    private void DeleteSelected()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _hours.Count)
        {
            _status.ForeColor = Theme.Muted;
            _status.Text = L.T("Selecciona una hora en la lista.", "Select an hour in the list.");
            return;
        }
        _hours.RemoveAt(i);
        Save();
        Fill();
        _status.ForeColor = Theme.Green;
        _status.Text = L.T("Eliminada.", "Deleted.");
    }

    private void ResetDefaults()
    {
        _hours.Clear();
        _hours.AddRange(TreasureHour.Defaults.Select(h => h.Copy()));
        _monitor.SetTreasureHours(null);
        Fill();
        _status.ForeColor = Theme.Green;
        _status.Text = L.T("Lista de origen restablecida.", "Original list restored.");
    }

    private void Save() => _monitor.SetTreasureHours(_hours.Select(h => h.Copy()).ToList());

    private Label Lbl(string text, Font font, Color color, Point at, Size size)
    {
        var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = BackColor, Location = at, Size = size };
        Controls.Add(l);
        return l;
    }
}
