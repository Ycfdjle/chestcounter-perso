using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>
/// Detalle por jugador de una partida: tus cofres (exactos) y los de cada compañero. Tu replay solo contiene lo que
/// pasa cerca de ti, así que un compañero lejano sale por debajo (≥); aquí se puede escribir su número real, que se
/// guarda en config.json y sustituye al visto en el total del equipo, el informe y la probabilidad del token.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MatchDetailsForm : Form
{
    private const int W = 460, Pad = 20, MaxMates = 3;
    private const int ColName = 14, ColSeen = 200, ColFix = 310;

    private readonly Monitor _monitor;
    private readonly List<MatchLine> _matches;
    private readonly ComboBox _picker;
    private readonly Label _youSeen;
    private readonly Label[] _mateNames = new Label[MaxMates], _mateSeen = new Label[MaxMates];
    private readonly TextBox[] _mateFix = new TextBox[MaxMates];
    private readonly Card[] _mateFixBox = new Card[MaxMates];
    private readonly Label _message, _more, _team, _saved;
    private MatchLine? _shown;
    private bool _loading, _dirty;

    public MatchDetailsForm(Monitor monitor)
    {
        _monitor = monitor;
        _matches = monitor.RecentMatches().ToList();

        Text = L.T("Detalle de la partida", "Match details");
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

        Lbl(this, L.T("Detalle de la partida", "Match details"), Theme.Text(12f, FontStyle.Bold), Theme.Ink, new Point(Pad, Pad), new Size(W - 2 * Pad, 24));

        _picker = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel,
            ForeColor = Theme.Ink,
            Font = Theme.Text(9f),
            Location = new Point(Pad, Pad + 32),
            Size = new Size(W - 2 * Pad, 24),
        };
        foreach (var m in _matches)
        {
            _picker.Items.Add(Describe(m));
        }
        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            SaveIfDirty();
            Display(_picker.SelectedIndex >= 0 && _picker.SelectedIndex < _matches.Count ? _matches[_picker.SelectedIndex] : null);
        };
        Controls.Add(_picker);

        // Tabla: jugador · visto en tu replay · corregido.
        var card = new Card { Location = new Point(Pad, Pad + 68), Size = new Size(W - 2 * Pad, 46 + 34 * (MaxMates + 1)), Radius = 14, BackColor = Theme.Card };
        Controls.Add(card);
        Lbl(card, L.T("Jugador", "Player"), Theme.Text(8f), Theme.Muted, new Point(ColName, 12), new Size(ColSeen - ColName - 8, 16));
        Lbl(card, L.T("Visto / sincronizado", "Seen / synced"), Theme.Text(8f), Theme.Muted, new Point(ColSeen, 12), new Size(ColFix - ColSeen - 8, 16));
        Lbl(card, L.T("Corregido", "Corrected"), Theme.Text(8f), Theme.Muted, new Point(ColFix, 12), new Size(card.Width - ColFix - 14, 16));

        var row = 40;
        Lbl(card, L.T("Tú", "You"), Theme.Text(9.5f, FontStyle.Bold), Theme.Amber, new Point(ColName, row + 6), new Size(ColSeen - ColName - 8, 20));
        _youSeen = Lbl(card, "", Theme.Text(11f, FontStyle.Bold), Theme.Amber, new Point(ColSeen, row + 4), new Size(ColFix - ColSeen - 8, 22));
        Lbl(card, L.T("exacto", "exact"), Theme.Text(8.5f), Theme.Muted, new Point(ColFix, row + 7), new Size(card.Width - ColFix - 14, 18));

        for (var i = 0; i < MaxMates; i++)
        {
            var y = row + 34 * (i + 1);
            _mateNames[i] = Lbl(card, "", Theme.Text(9.5f), Theme.Ink, new Point(ColName, y + 6), new Size(ColSeen - ColName - 8, 20));
            _mateSeen[i] = Lbl(card, "", Theme.Text(11f, FontStyle.Bold), Theme.Ink, new Point(ColSeen, y + 4), new Size(ColFix - ColSeen - 8, 22));
            _mateFixBox[i] = new Card { Location = new Point(ColFix, y + 2), Size = new Size(card.Width - ColFix - 14, 28), Radius = 9, BackColor = Theme.Panel };
            _mateFix[i] = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel,
                ForeColor = Theme.Cyan,
                Font = Theme.Text(10.5f, FontStyle.Bold),
                Location = new Point(9, 5),
                Width = _mateFixBox[i].Width - 18,
                MaxLength = 4,
            };
            _mateFix[i].KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
            _mateFix[i].TextChanged += (_, _) =>
            {
                if (_loading) return;
                _dirty = true;
                _saved!.Text = "";
                UpdateTeam();
            };
            _mateFixBox[i].Controls.Add(_mateFix[i]);
            card.Controls.Add(_mateFixBox[i]);
        }
        // Partida sin compañeros (solitario, o analizada antes de guardarlos): un mensaje en lugar de las filas.
        _message = Lbl(card, "", Theme.Text(8.75f), Theme.Muted, new Point(ColName, row + 40), new Size(card.Width - 2 * ColName, 60));

        var y0 = card.Bottom + 8;
        _more = Lbl(this, "", Theme.Text(8f), Theme.Muted, new Point(Pad, y0), new Size(W - 2 * Pad, 16));
        _team = Lbl(this, "", Theme.Text(10f, FontStyle.Bold), Theme.Cyan, new Point(Pad, y0 + 18), new Size(W - 2 * Pad, 20));
        Lbl(this, L.T("Tu replay solo ve lo que pasa cerca de ti: los compañeros lejos salen por debajo (≥). Pregúntales su número real y escríbelo en «Corregido»; vacío = se usa el del replay.",
                "Your replay only sees what happens near you: far-away teammates are undercounted (≥). Ask them their real number and type it in «Corrected»; empty = the replay's figure is used."),
            Theme.Text(8.5f), Theme.Muted, new Point(Pad, y0 + 44), new Size(W - 2 * Pad, 48));

        var by = y0 + 100;
        var save = new PillButton
        {
            Text = L.T("Guardar", "Save"),
            Size = new Size(110, 36),
            Location = new Point(W - Pad - 110, by),
            Radius = 11,
            BackColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            ForeColor = Theme.AccentInk,
        };
        save.Click += (_, _) => Save();
        var clear = new PillButton { Text = L.T("Borrar correcciones", "Clear corrections"), Size = new Size(160, 36), Location = new Point(W - Pad - 110 - 8 - 160, by), Radius = 11 };
        clear.Click += (_, _) => ClearCorrections();
        var close = new PillButton { Text = L.T("Cerrar", "Close"), Size = new Size(90, 36), Location = new Point(Pad, by), Radius = 11, DialogResult = DialogResult.Cancel };
        Controls.Add(save);
        Controls.Add(clear);
        Controls.Add(close);
        _saved = Lbl(this, "", Theme.Text(8.5f), Theme.Green, new Point(W - Pad - 110 - 8 - 160, by + 40), new Size(270, 16));
        AcceptButton = save;
        CancelButton = close;
        ClientSize = new Size(W, by + 36 + 22 + Pad);

        FormClosing += (_, _) => SaveIfDirty(); // cerrar con cambios sin guardar también los guarda

        if (_matches.Count > 0)
        {
            _picker.SelectedIndex = 0; // dispara Display
        }
        else
        {
            Display(null);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Chrome(Handle);
    }

    /// <summary>"13/11 22:54 · en curso · 3.º · 12 cofres" para el selector.</summary>
    private static string Describe(MatchLine m)
    {
        var parts = new List<string> { L.DateTimeShort(m.When) };
        if (m.Live) parts.Add(L.T("en curso", "in progress"));
        if (m.Placement is { } place) parts.Add(L.Ordinal(place));
        var team = m.Mine + (m.Mates?.Sum(t => t.Corrected ?? t.Synced ?? t.Seen) ?? 0);
        parts.Add(m.Mates is { Count: > 0 }
            ? L.F("equipo {0}", "team {0}", L.N(team))
            : L.Count(m.Mine, "cofre", "cofres", "chest", "chests"));
        return string.Join("  ·  ", parts);
    }

    private void Display(MatchLine? m)
    {
        _loading = true;
        _dirty = false;
        _shown = m;
        _saved.Text = "";
        _youSeen.Text = m != null ? L.N(m.Mine) : "—";

        var mates = m?.Mates ?? Array.Empty<MateLine>();
        var message = m == null ? L.T("Sin partidas todavía.", "No matches yet.")
            : m.Mates == null ? L.T("Partida analizada antes de guardar compañeros.", "Match analyzed before teammates were saved.")
            : mates.Count == 0 ? L.T("Partida en solitario: sin compañeros.", "Solo match: no teammates.")
            : null;
        _message.Text = message ?? "";
        _message.Visible = message != null;

        for (var i = 0; i < MaxMates; i++)
        {
            var mate = message == null && i < mates.Count ? mates[i] : null;
            _mateNames[i].Text = mate?.Name ?? "";
            // Synchro squad : le compte exact envoyé par le mate (✓) remplace le minimum vu dans ton replay (≥).
            _mateSeen[i].Text = mate == null ? "" : mate.Synced is { } exact ? "✓ " + L.N(exact) : "≥ " + L.N(mate.Seen);
            _mateSeen[i].ForeColor = mate?.Synced != null ? Theme.Green : Theme.Ink;
            _mateFix[i].Text = mate?.Corrected is { } fixedCount ? fixedCount.ToString(L.Culture) : "";
            _mateNames[i].Visible = _mateSeen[i].Visible = _mateFixBox[i].Visible = mate != null;
        }
        _more.Text = message == null && mates.Count > MaxMates
            ? L.F("+{0} compañeros más (ver el informe)", "+{0} more teammates (see the report)", mates.Count - MaxMates)
            : "";
        _loading = false;
        UpdateTeam();
    }

    /// <summary>Total del equipo con lo escrito ahora mismo (aunque aún no esté guardado).</summary>
    private void UpdateTeam()
    {
        var shown = _shown;
        if (shown == null)
        {
            _team.Text = "";
            return;
        }
        if (shown.Mates is not { Count: > 0 } mates)
        {
            _team.Text = L.F("Equipo: {0}", "Team: {0}", L.N(shown.Mine));
            return;
        }
        var matesTotal = 0;
        for (var i = 0; i < mates.Count; i++)
        {
            // Case vide = pas de correction : le compte synchronisé, sinon celui du replay.
            matesTotal += i < MaxMates ? Typed(i) ?? mates[i].Synced ?? mates[i].Seen : mates[i].Corrected ?? mates[i].Synced ?? mates[i].Seen;
        }
        _team.Text = L.F("Equipo: {0}  (tú {1} + compañeros {2})", "Team: {0}  (you {1} + teammates {2})",
            L.N(shown.Mine + matesTotal), L.N(shown.Mine), L.N(matesTotal));
    }

    /// <summary>Número escrito en la fila de un compañero, o null si la casilla está vacía.</summary>
    private int? Typed(int i) =>
        int.TryParse(_mateFix[i].Text, System.Globalization.NumberStyles.None, L.Culture, out var n) ? n : null;

    private void SaveIfDirty()
    {
        if (_dirty)
        {
            Save();
        }
    }

    /// <summary>Guarda las casillas rellenas de la partida mostrada (las vacías vuelven al número del replay).</summary>
    private void Save()
    {
        var shown = _shown;
        if (shown?.Mates is not { Count: > 0 } mates)
        {
            return;
        }
        var values = new Dictionary<string, int>();
        // Los compañeros que no caben en la tabla conservan su corrección anterior.
        for (var i = 0; i < mates.Count; i++)
        {
            var value = i < MaxMates ? Typed(i) : mates[i].Corrected;
            if (value is { } v)
            {
                values[mates[i].Key] = v;
            }
        }
        _monitor.SetMateCorrections(shown.SessionId, values);
        Remember(shown, values);
        _dirty = false;
        _saved.Text = L.T("Guardado ✓", "Saved ✓");
    }

    private void ClearCorrections()
    {
        var shown = _shown;
        if (shown?.Mates is not { Count: > 0 })
        {
            return;
        }
        _monitor.SetMateCorrections(shown.SessionId, new Dictionary<string, int>());
        var cleared = Remember(shown, new Dictionary<string, int>());
        Display(cleared);
        _saved.Text = L.T("Guardado ✓", "Saved ✓");
    }

    /// <summary>Actualiza la copia local de la partida (y su texto en el selector) tras guardar.</summary>
    private MatchLine Remember(MatchLine m, IReadOnlyDictionary<string, int> values)
    {
        var updated = m with
        {
            Mates = m.Mates?.Select(t => t with { Corrected = values.TryGetValue(t.Key, out var v) ? v : null }).ToList(),
        };
        var index = _matches.IndexOf(m);
        if (index >= 0)
        {
            _matches[index] = updated;
            // Cambiar el texto de una opción puede mover la selección: se restaura sin volver a cargar la partida.
            var selected = _picker.SelectedIndex;
            _loading = true;
            _picker.Items[index] = Describe(updated);
            if (_picker.SelectedIndex != selected)
            {
                _picker.SelectedIndex = selected;
            }
            _loading = false;
        }
        _shown = updated;
        return updated;
    }

    private static Label Lbl(Control parent, string text, Font font, Color color, Point at, Size size)
    {
        var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = parent.BackColor, Location = at, Size = size };
        parent.Controls.Add(l);
        return l;
    }
}
