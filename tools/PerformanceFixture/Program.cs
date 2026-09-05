using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RapidPcUse.PerformanceFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = FixtureOptions.Parse(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new PerformanceFixtureForm(options));
    }
}

internal sealed record FixtureOptions(string FixtureId, string RunToken)
{
    private static readonly Regex Identifier = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static FixtureOptions Parse(IReadOnlyList<string> args)
    {
        string? fixtureId = null;
        string? runToken = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--fixture" when index + 1 < args.Count:
                    fixtureId = args[++index];
                    break;
                case "--run-token" when index + 1 < args.Count:
                    runToken = args[++index];
                    break;
                default:
                    throw new ArgumentException("Usage: --fixture <id> --run-token <id>.");
            }
        }

        if (fixtureId is not ("click-ladder-v1" or "form-tab-v1") ||
            string.IsNullOrWhiteSpace(runToken) ||
            !Identifier.IsMatch(runToken))
        {
            throw new ArgumentException("The fixture ID or run token is invalid.");
        }

        return new FixtureOptions(fixtureId, runToken);
    }
}

internal sealed class PerformanceFixtureForm : Form
{
    private static readonly int[] ClickSequence = [5, 12, 2, 15, 8, 1, 14, 4, 10, 7, 16, 3, 11, 6, 13, 9];
    private static readonly Dictionary<string, string> FormAnswers = new(StringComparer.Ordinal)
    {
        ["First name"] = "Ada",
        ["Last name"] = "Lovelace",
        ["Department"] = "Performance",
        ["Code"] = "RPU-2048",
        ["Notes"] = "latency",
    };

    private readonly FixtureOptions _options;
    private readonly Stopwatch _activeTimer = new();
    private readonly Label _status = new();
    private readonly Dictionary<string, TextBox> _fields = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _readyAt = DateTimeOffset.UtcNow;
    private readonly System.Windows.Forms.Timer _foregroundTimer = new() { Interval = 50 };
    private int _foregroundAttempts;
    private int _clickIndex;
    private int _usefulActions;
    private int _incorrectActions;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string _state = "ready";

    internal PerformanceFixtureForm(FixtureOptions options)
    {
        _options = options;
        Text = $"Rapid PC Performance Fixture — {options.FixtureId}";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 760);
        MinimumSize = Size;
        MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(245, 247, 250);
        Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point);

        Controls.Add(options.FixtureId == "click-ladder-v1" ? BuildClickLadder() : BuildFormFixture());
        _foregroundTimer.Tick += (_, _) =>
        {
            _foregroundAttempts++;
            if (TryActivateWindow(Handle))
            {
                FocusInitialControl();
                StopForegroundReadinessLoop();
            }
            else if (_foregroundAttempts >= 20)
            {
                StopForegroundReadinessLoop();
            }
        };
        Shown += (_, _) =>
        {
            TopMost = true;
            WriteState();
            Activate();
            if (TryActivateWindow(Handle))
            {
                FocusInitialControl();
            }
            _foregroundTimer.Start();
        };
    }

    private void FocusInitialControl()
    {
        if (_options.FixtureId == "form-tab-v1" && _fields.TryGetValue("First name", out var firstName))
        {
            _ = firstName.Focus();
        }
    }

    private void StopForegroundReadinessLoop()
    {
        TopMost = false;
        _foregroundTimer.Stop();
        _foregroundTimer.Dispose();
    }

    private TableLayoutPanel BuildClickLadder()
    {
        var root = RootLayout();
        root.Controls.Add(Header(
            "CLICK LADDER",
            "Click every numbered tile in this exact order. Do not click disabled tiles.",
            string.Join("  →  ", ClickSequence)), 0, 0);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            Margin = new Padding(28, 14, 28, 14),
        };
        for (var index = 0; index < 4; index++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        }

        for (var number = 1; number <= 16; number++)
        {
            var tileNumber = number;
            var button = new Button
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Dock = DockStyle.Fill,
                Margin = new Padding(9),
                Font = new Font("Segoe UI", 24F, FontStyle.Bold, GraphicsUnit.Point),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                AccessibleName = $"Tile {number}",
                TabIndex = number - 1,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(180, 188, 200);
            button.FlatAppearance.BorderSize = 2;
            button.Click += (_, _) => ClickTile(tileNumber, button);
            grid.Controls.Add(button, (number - 1) % 4, (number - 1) / 4);
        }

        root.Controls.Add(grid, 0, 1);
        root.Controls.Add(StatusPanel("Ready — next tile: 5"), 0, 2);
        return root;
    }

    private TableLayoutPanel BuildFormFixture()
    {
        var root = RootLayout();
        root.Controls.Add(Header(
            "TAB FORM",
            "Enter the five values exactly, then activate Submit. Keyboard navigation is encouraged.",
            "Ada  •  Lovelace  •  Performance  •  RPU-2048  •  latency"), 0, 0);

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = FormAnswers.Count + 1,
            Padding = new Padding(150, 28, 150, 18),
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        var row = 0;
        foreach (var answer in FormAnswers)
        {
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / (FormAnswers.Count + 1)));
            var label = new Label
            {
                Text = answer.Key,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 18, 0),
            };
            var field = new TextBox
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 16, 0, 16),
                Font = new Font("Segoe UI", 16F, FontStyle.Regular, GraphicsUnit.Point),
                AccessibleName = answer.Key,
                TabIndex = row,
            };
            field.TextChanged += (_, _) => StartActiveTimer();
            _fields.Add(answer.Key, field);
            form.Controls.Add(label, 0, row);
            form.Controls.Add(field, 1, row);
            row++;
        }

        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / (FormAnswers.Count + 1)));
        var submit = new Button
        {
            Text = "Submit",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 12),
            Font = new Font("Segoe UI", 16F, FontStyle.Bold, GraphicsUnit.Point),
            BackColor = Color.FromArgb(35, 99, 235),
            ForeColor = Color.White,
            AccessibleName = "Submit form",
            TabIndex = row,
        };
        submit.Click += (_, _) => SubmitForm(submit);
        form.Controls.Add(submit, 1, row);

        root.Controls.Add(form, 0, 1);
        root.Controls.Add(StatusPanel("Ready — enter the displayed values"), 0, 2);
        return root;
    }

    private static TableLayoutPanel RootLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        return root;
    }

    private static TableLayoutPanel Header(string title, string instruction, string payload)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(20, 31, 52),
            Padding = new Padding(24, 12, 24, 10),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
        });
        panel.Controls.Add(new Label
        {
            Text = instruction,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(205, 214, 230),
            TextAlign = ContentAlignment.MiddleLeft,
        });
        panel.Controls.Add(new Label
        {
            Text = payload,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(109, 207, 246),
            Font = new Font("Consolas", 13F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
        });
        return panel;
    }

    private Label StatusPanel(string initialText)
    {
        _status.Text = initialText;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Segoe UI", 16F, FontStyle.Bold, GraphicsUnit.Point);
        _status.BackColor = Color.FromArgb(225, 231, 240);
        _status.ForeColor = Color.FromArgb(25, 38, 60);
        return _status;
    }

    private void ClickTile(int number, Button button)
    {
        StartActiveTimer();
        if (_clickIndex >= ClickSequence.Length || number != ClickSequence[_clickIndex])
        {
            _incorrectActions++;
            _status.Text = $"Incorrect — next tile remains {ClickSequence[_clickIndex]}";
            _status.BackColor = Color.FromArgb(254, 226, 226);
            return;
        }

        _clickIndex++;
        _usefulActions = _clickIndex;
        button.Enabled = false;
        if (_clickIndex == ClickSequence.Length)
        {
            Complete();
            return;
        }

        _status.Text = $"Progress {_clickIndex}/{ClickSequence.Length} — next tile: {ClickSequence[_clickIndex]}";
        _status.BackColor = Color.FromArgb(220, 252, 231);
    }

    private void SubmitForm(Button submit)
    {
        StartActiveTimer();
        var correctFields = FormAnswers.Count(answer =>
            string.Equals(_fields[answer.Key].Text, answer.Value, StringComparison.Ordinal));
        _usefulActions = correctFields;
        if (correctFields != FormAnswers.Count)
        {
            _incorrectActions++;
            _status.Text = $"Not complete — {correctFields}/{FormAnswers.Count} fields are exact";
            _status.BackColor = Color.FromArgb(254, 226, 226);
            WriteState();
            return;
        }

        _usefulActions++;
        submit.Enabled = false;
        foreach (var field in _fields.Values)
        {
            field.ReadOnly = true;
        }

        Complete();
    }

    private void StartActiveTimer()
    {
        if (_activeTimer.IsRunning || _state == "completed")
        {
            return;
        }

        _state = "running";
        _startedAt = DateTimeOffset.UtcNow;
        _activeTimer.Start();
    }

    private void Complete()
    {
        _activeTimer.Stop();
        _completedAt = DateTimeOffset.UtcNow;
        _state = "completed";
        _status.Text = $"COMPLETE — {_usefulActions} useful actions";
        _status.BackColor = Color.FromArgb(187, 247, 208);
        _status.ForeColor = Color.FromArgb(20, 83, 45);
        WriteState();
    }

    private void WriteState()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RapidPcUse",
            "PerformanceFixture");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        var temporary = Path.Combine(directory, $"state-{Environment.ProcessId}.tmp");
        var payload = new
        {
            schemaVersion = 1,
            fixtureId = _options.FixtureId,
            runToken = _options.RunToken,
            status = _state,
            usefulActions = _usefulActions,
            incorrectActions = _incorrectActions,
            readyAt = _readyAt.ToString("O"),
            startedAt = _startedAt?.ToString("O"),
            completedAt = _completedAt?.ToString("O"),
            activeElapsedMs = _activeTimer.ElapsedMilliseconds,
        };
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload));
        File.Move(temporary, path, true);
    }

    private static bool TryActivateWindow(nint target)
    {
        if (GetForegroundWindow() == target)
        {
            return true;
        }

        _ = SetForegroundWindow(target);
        if (GetForegroundWindow() == target)
        {
            return true;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attachedForeground = AttachInputThread(currentThread, foregroundThread);
        var attachedTarget = AttachInputThread(currentThread, targetThread);
        try
        {
            _ = BringWindowToTop(target);
            _ = SetActiveWindow(target);
            _ = SetForegroundWindow(target);
            if (GetForegroundWindow() != target)
            {
                SwitchToThisWindow(target, true);
            }

            return GetForegroundWindow() == target;
        }
        finally
        {
            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static bool AttachInputThread(uint currentThread, uint otherThread)
        => otherThread != 0 && otherThread != currentThread &&
           AttachThreadInput(currentThread, otherThread, true);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint window);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
