using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>
/// «+ Añadir» de la ventana principal: suma (o quita) cofres abiertos antes de usar el contador. Es el mismo número
/// que «Cofres antes del contador» de Ajustes: cuenta en el total y en la probabilidad del token, pero no en la
/// media por partida, ni en hoy, ni en los récords.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AddChestsForm : Form
{
    private const int W = 380, Pad = 20;
    private readonly Monitor _monitor;
    private readonly TextBox _amount;
    private readonly Label _current;
    private readonly Label _error;

    public AddChestsForm(Monitor monitor)
    {
        _monitor = monitor;
        Text = L.T("Añadir cofres", "Add chests");
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

        Lbl(L.T("Añadir cofres", "Add chests"), Theme.Text(12f, FontStyle.Bold), Theme.Ink, new Point(Pad, Pad), new Size(W - 2 * Pad, 24));
        Lbl(L.T("Cofres que abriste antes de usar el contador (vale una estimación). Cuentan en el total y en la probabilidad del token, pero no en la media, ni en hoy, ni en los récords.",
                "Chests you opened before using the counter (an estimate is fine). They count in the total and in the token chance, but not in the average, today or records."),
            Theme.Text(8.75f), Theme.Muted, new Point(Pad, Pad + 28), new Size(W - 2 * Pad, 62));

        // Cifra a sumar, grande y en ámbar, como en Ajustes.
        var box = new Card { Location = new Point(Pad, Pad + 96), Size = new Size(W - 2 * Pad, 58), Radius = 14, BackColor = Theme.Card };
        Controls.Add(box);
        _amount = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.Amber,
            Font = Theme.Text(22f, FontStyle.Bold),
            Location = new Point(14, 12),
            Width = 180,
            MaxLength = 7,
        };
        _amount.KeyPress += (_, e) => e.Handled = !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar);
        box.Controls.Add(_amount);
        box.Controls.Add(new Label
        {
            Text = L.T("cofres", "chests"),
            Font = Theme.Text(9f),
            ForeColor = Theme.Muted,
            BackColor = Theme.Card,
            Location = new Point(200, 22),
            Size = new Size(box.Width - 214, 18),
        });

        _current = Lbl("", Theme.Text(8.75f), Theme.Ink, new Point(Pad, Pad + 162), new Size(W - 2 * Pad, 18));
        _error = Lbl("", Theme.Text(8.5f), Theme.Red, new Point(Pad, Pad + 182), new Size(W - 2 * Pad, 18));

        // Botones: añadir (principal), quitar y cerrar.
        var y = Pad + 208;
        var add = new PillButton
        {
            Text = L.T("Añadir", "Add"),
            Size = new Size(110, 36),
            Location = new Point(W - Pad - 110, y),
            Radius = 11,
            BackColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            ForeColor = Theme.AccentInk,
        };
        add.Click += (_, _) => Apply(+1);
        var remove = new PillButton { Text = L.T("Quitar", "Remove"), Size = new Size(100, 36), Location = new Point(W - Pad - 110 - 8 - 100, y), Radius = 11 };
        remove.Click += (_, _) => Apply(-1);
        var close = new PillButton { Text = L.T("Cerrar", "Close"), Size = new Size(90, 36), Location = new Point(Pad, y), Radius = 11, DialogResult = DialogResult.Cancel };
        Controls.Add(add);
        Controls.Add(remove);
        Controls.Add(close);
        AcceptButton = add;
        CancelButton = close;
        ActiveControl = _amount;
        ClientSize = new Size(W, y + 36 + Pad);
        ShowCurrent(_monitor.InitialChests);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Chrome(Handle);
    }

    /// <summary>Suma (+1) o resta (−1) la cifra escrita. La ventana se queda abierta para ver el nuevo total.</summary>
    private void Apply(int sign)
    {
        if (!int.TryParse(_amount.Text, System.Globalization.NumberStyles.None, L.Culture, out var n) || n <= 0)
        {
            _error.Text = L.T("Escribe un número de cofres.", "Type a number of chests.");
            return;
        }
        _error.Text = "";
        // Se calcula aquí: el monitor guarda el valor nuevo enseguida, pero lo publica en su hilo un poco después.
        var updated = Math.Clamp(_monitor.InitialChests + sign * n, 0, Monitor.MaxInitialChests);
        _monitor.SetInitialChests(updated);
        _amount.Text = "";
        ShowCurrent(updated);
    }

    private void ShowCurrent(int added) =>
        _current.Text = L.F("Ya añadidos: {0}", "Already added: {0}", L.N(added));

    private Label Lbl(string text, Font font, Color color, Point at, Size size)
    {
        var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = BackColor, Location = at, Size = size };
        Controls.Add(l);
        return l;
    }
}
