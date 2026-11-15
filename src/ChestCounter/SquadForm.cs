using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>
/// « Squad » : activer la synchro des coffres avec ses mates. Tout le monde met le même code (une seule fois) ;
/// ensuite chaque ChestCounter envoie ses coffres exacts et reçoit ceux des autres, partie par partie, sans rien
/// faire après chaque game.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SquadForm : Form
{
    private const int W = 440, Pad = 20;
    private readonly Monitor _monitor;
    private readonly CheckBox _enabled;
    private readonly TextBox _code;
    private readonly Label _status, _mates, _error;
    private readonly System.Windows.Forms.Timer _refresh;

    public SquadForm(Monitor monitor)
    {
        _monitor = monitor;
        Text = L.T("Sincronización de escuadra", "Squad sync");
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

        Lbl(L.T("Sincronización de escuadra", "Squad sync"), Theme.Text(12f, FontStyle.Bold), Theme.Ink, new Point(Pad, Pad), new Size(W - 2 * Pad, 24));
        Lbl(L.T("Cada compañero abre ChestCounter en su PC con el mismo código. Cada uno envía sus cofres exactos y recibe los de los demás, partida por partida: «≥» pasa a «✓».",
                "Each teammate runs ChestCounter on their PC with the same code. Everyone sends their exact chests and receives the others', match by match: «≥» becomes «✓»."),
            Theme.Text(8.75f), Theme.Muted, new Point(Pad, Pad + 28), new Size(W - 2 * Pad, 50));

        _enabled = new CheckBox
        {
            Text = L.T("Activar la sincronización", "Turn on sync"),
            Checked = monitor.Config.SquadSync,
            Location = new Point(Pad, Pad + 84),
            Size = new Size(W - 2 * Pad, 24),
            ForeColor = Theme.Ink,
            Font = Theme.Text(9.5f, FontStyle.Bold),
        };
        Controls.Add(_enabled);

        Lbl(L.T("Código de escuadra (el mismo para todos)", "Squad code (the same for everyone)"), Theme.Text(8f), Theme.Muted, new Point(Pad, Pad + 116), new Size(W - 2 * Pad, 16));
        var box = new Card { Location = new Point(Pad, Pad + 134), Size = new Size(W - 2 * Pad - 196, 34), Radius = 10, BackColor = Theme.Card };
        _code = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Card,
            ForeColor = Theme.Mint,
            Font = Theme.Text(11f, FontStyle.Bold),
            Location = new Point(10, 7),
            Width = box.Width - 20,
            MaxLength = 40,
            Text = monitor.Config.SquadCode ?? "",
        };
        box.Controls.Add(_code);
        Controls.Add(box);
        var generate = new PillButton { Text = L.T("Nuevo código", "New code"), Size = new Size(104, 34), Location = new Point(box.Right + 8, box.Top), Radius = 10 };
        generate.Click += (_, _) => _code.Text = SquadSync.NewCode();
        var copy = new PillButton { Text = L.T("Copiar", "Copy"), Size = new Size(76, 34), Location = new Point(generate.Right + 8, box.Top), Radius = 10 };
        copy.Click += (_, _) =>
        {
            if (_code.Text.Length > 0)
            {
                try
                {
                    Clipboard.SetText(_code.Text);
                    ShowError(L.T("Código copiado: envíalo a tus compañeros.", "Code copied: send it to your teammates."), Theme.Green);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    _code.SelectAll();
                }
            }
        };
        Controls.Add(generate);
        Controls.Add(copy);

        _error = Lbl("", Theme.Text(8.5f), Theme.Red, new Point(Pad, Pad + 172), new Size(W - 2 * Pad, 18));
        _status = Lbl("", Theme.Text(9f, FontStyle.Bold), Theme.Ink, new Point(Pad, Pad + 196), new Size(W - 2 * Pad, 20));
        _mates = Lbl("", Theme.Text(8.75f), Theme.Muted, new Point(Pad, Pad + 218), new Size(W - 2 * Pad, 38));
        Lbl(L.T("Pasa por el servicio gratuito ntfy.sh: quien conozca el código puede ver los pseudos y los cofres. Usa un código largo (botón «Nuevo código»).",
                "Goes through the free ntfy.sh service: anyone who knows the code can see the names and chests. Use a long code («New code» button)."),
            Theme.Text(8f), Theme.Muted, new Point(Pad, Pad + 260), new Size(W - 2 * Pad, 34));

        var y = Pad + 302;
        var save = new PillButton
        {
            Text = L.T("Guardar", "Save"),
            Size = new Size(110, 36),
            Location = new Point(W - Pad - 110, y),
            Radius = 11,
            BackColor = Theme.Accent,
            HoverColor = Theme.AccentHover,
            ForeColor = Theme.AccentInk,
        };
        save.Click += (_, _) => Save();
        var close = new PillButton { Text = L.T("Cerrar", "Close"), Size = new Size(90, 36), Location = new Point(Pad, y), Radius = 11, DialogResult = DialogResult.Cancel };
        Controls.Add(save);
        Controls.Add(close);
        AcceptButton = save;
        CancelButton = close;
        ClientSize = new Size(W, y + 36 + Pad);

        // L'état de la synchro change en arrière-plan : on le relit toutes les 2 s tant que la fenêtre est ouverte.
        _refresh = new System.Windows.Forms.Timer { Interval = 2000 };
        _refresh.Tick += (_, _) => ShowStatus();
        _refresh.Start();
        FormClosed += (_, _) => _refresh.Dispose();
        ShowStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.Chrome(Handle);
    }

    private void Save()
    {
        var code = _code.Text.Trim();
        if (_enabled.Checked && !SquadSync.IsValidCode(code))
        {
            ShowError(L.T("El código debe tener de 6 a 40 letras o cifras (sin espacios).", "The code must be 6 to 40 letters or digits (no spaces)."), Theme.Red);
            return;
        }
        _monitor.SetSquad(_enabled.Checked, code.Length > 0 ? code : null);
        ShowError(_enabled.Checked ? L.T("Guardado: sincronizando…", "Saved: syncing…") : L.T("Guardado: sincronización desactivada.", "Saved: sync turned off."), Theme.Green);
    }

    private void ShowError(string text, Color color)
    {
        _error.ForeColor = color;
        _error.Text = text;
    }

    private void ShowStatus()
    {
        var squad = _monitor.Squad;
        _status.Text = squad.Status;
        _status.ForeColor = squad.LastSyncLocal != null && _monitor.Config.SquadSync ? Theme.Mint : Theme.Ink;
        var mates = squad.Mates;
        _mates.Text = !_monitor.Config.SquadSync ? ""
            : mates.Count == 0 ? L.T("Ningún compañero recibido todavía. Ellos también tienen que activar la sincronización con este código.",
                "No teammate received yet. They also need to turn on sync with this code.")
            : L.F("Recibidos: {0}", "Received: {0}", string.Join(", ", mates.Select(kv => L.F("{0} ({1} partidas)", "{0} ({1} matches)", kv.Key, kv.Value))));
    }

    private Label Lbl(string text, Font font, Color color, Point at, Size size)
    {
        var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = BackColor, Location = at, Size = size };
        Controls.Add(l);
        return l;
    }
}
