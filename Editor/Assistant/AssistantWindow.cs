using System.Diagnostics;
using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Editor.Ui;

namespace Editor.Assistant;

// Help > AI Assistant: a chat with an AI model from inside the editor, using the Claude API with the user's own API key.
//
// Built to be safe to ship in an open-source engine:
//   - there is no key of ours anywhere: each user brings their own, which is stored only on their computer
//   - it is off until the user has read and agreed to a notice saying what is sent, where, and who pays
//   - nothing is sent until they press Send, and only what they typed plus what they ticked (the open script, the
//     selected object); a script's name goes, never a file path
//   - nothing is written to a project or scene, and the conversation is kept in memory only
// It never changes the project by itself: answers are shown, and the user copies what they want.
public sealed class AssistantWindow
{
    private sealed record Entry(string Role, string Text, string? Note);   // Role is "user", "assistant" or "error"

    private static readonly string EditorVersion =
        typeof(AssistantWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
    private static readonly HttpClient Http = AssistantClient.CreateClient(EditorVersion);

    private static float4 UserColor => Theme.Pick(0x9CDCFE, 0x0B5CAD);
    private static float4 AssistantColor => Theme.Pick(0xB5CEA8, 0x2E7D32);

    private readonly string _folder;
    private AssistantSettings _settings = new();
    private bool _open;
    private bool _agree;
    private bool _showSettings;
    private string _keyInput = "";
    private string _prompt = "";
    private string _notice = "";

    private readonly List<Entry> _log = new();                  // what is shown
    private readonly List<ChatMessage> _conversation = new();   // what was sent, and the answers
    private Task<AssistantReply>? _pending;
    private CancellationTokenSource? _cts;
    private string _lastQuestion = "";
    private int _stickToEnd;                                    // frames left to keep the chat scrolled to the newest message
    private bool _wasTyping;

    public AssistantWindow(string? settingsFolder = null) => _folder = settingsFolder ?? AssistantSettings.DefaultFolder;

    public bool IsOpen => _open;

    public void Open()
    {
        _open = true;
        _settings = AssistantSettings.Load(_folder);
        _agree = false;
        _notice = "";
    }

    public void Close() => _open = false;

    public void Draw(Gui g, AssistantContext context)
    {
        if (!_open) return;
        Poll();

        float w = Math.Min(660f, g.Size.X - 40f), h = Math.Min(620f, g.Size.Y - 80f);
        var rect = new Box(Math.Max(20f, g.Size.X - w - 30f), 50f, w, h);
        bool typing = g.IsTyping;
        if (!g.BeginWindow("AiAssistant", rect, "AI Assistant", WindowFlags.Closable | WindowFlags.NoScroll))
            _open = false;

        if (!_settings.HasConsented) DrawNotice(g);
        else if (AssistantKeyStore.Resolve(out _, _folder) is null || _showSettings) DrawKeyAndSettings(g);
        else DrawChat(g, context, typing);

        g.EndWindow();
        _wasTyping = g.IsTyping;
    }

    // ---- the notice ---------------------------------------------------------------------------------

    private void DrawNotice(Gui g)
    {
        g.BeginChild("notice", 0f, -118f);
        g.Text("AI Assistant (optional)", Gui.AccentHover);
        g.TextWrapped("Chat with an AI model from inside the editor, to help write and fix scripts. It uses the Claude API from " +
                      "Anthropic with YOUR OWN API key. Empire Engine has no key and no server of its own: nothing passes through us.");
        g.Spacing();
        g.Text("What is sent");
        g.TextWrapped("Only what you type and press Send on, plus the open script or the selected object's details if you tick " +
                      "them. A script's name is sent, never a file path. Nothing is sent until you press Send. It goes to Anthropic " +
                      "over the internet, and their terms and privacy policy apply to it. If your project is confidential, read them first.",
                      Gui.DimColor);
        g.Spacing();
        g.Text("What it costs");
        g.TextWrapped("Anthropic charges your own account for every question. Each answer shows how many tokens it used.", Gui.DimColor);
        g.Spacing();
        g.Text("Please keep in mind");
        g.TextWrapped("You must follow Anthropic's Usage Policy. Answers can be wrong or unsafe, so read code before you run it. " +
                      "Don't send passwords, keys, or other people's personal data.", Gui.DimColor);
        g.Spacing();
        g.Text("Your key");
        g.TextWrapped("It is saved in one file in your own user folder that only you can read, or you can set the " +
                      "ANTHROPIC_API_KEY environment variable instead and nothing is saved. It is never put in a project, a scene, " +
                      "or a log. You can remove it at any time. The conversation is kept in memory only and is gone when you close the editor.",
                      Gui.DimColor);
        g.Spacing();
        g.TextWrapped("Empire Engine is not made, endorsed or supported by Anthropic. Claude is a trademark of Anthropic, PBC.", Gui.DimColor);
        g.EndChild();

        // Outside the scrolling text, so they are always in view.
        if (g.Button("Usage Policy")) OpenUrl("https://www.anthropic.com/legal/aup");
        g.SameLine();
        if (g.Button("Privacy Policy")) OpenUrl("https://www.anthropic.com/legal/privacy");
        g.SameLine();
        if (g.Button("Commercial Terms")) OpenUrl("https://www.anthropic.com/legal/commercial-terms");
        g.Text("I have read this and agree:");
        g.Checkbox("I agree##agree", ref _agree);
        if (g.Button("Turn on the assistant", highlighted: true, enabled: _agree))
        {
            _settings.ConsentVersion = AssistantSettings.CurrentConsentVersion;
            SaveSettings();
        }
        g.SameLine();
        if (g.Button("Not now")) _open = false;
    }

    // ---- the key and settings ---------------------------------------------------------------------------

    private void DrawKeyAndSettings(Gui g)
    {
        string? existing = AssistantKeyStore.Resolve(out string source, _folder);
        g.Text(existing is null ? "Enter your Anthropic API key" : "Settings", Gui.AccentHover);
        g.Spacing();

        if (existing is not null && source == "environment")
        {
            g.TextWrapped("Using the key in the ANTHROPIC_API_KEY environment variable. Nothing is saved by the editor.", Gui.DimColor);
        }
        else
        {
            g.TextWrapped(existing is null
                ? "Create a key in the Anthropic Console (you need an account with credit). It is saved only on this computer."
                : "A key is saved on this computer. Enter a new one to replace it, or remove it.", Gui.DimColor);
            g.InputText("API key##key", ref _keyInput, 400, mask: true);
            if (g.Button("Save key", enabled: _keyInput.Length > 0, highlighted: existing is null))
            {
                try
                {
                    AssistantKeyStore.Save(_keyInput, _folder);
                    _keyInput = "";
                    _notice = "Key saved.";
                    _showSettings = false;
                }
                catch (Exception ex) when (ex is AssistantException or IOException or UnauthorizedAccessException)
                {
                    _notice = ex.Message;
                }
            }
            g.SameLine();
            if (g.Button("Get a key")) OpenUrl("https://console.anthropic.com/settings/keys");
            if (existing is not null)
            {
                g.SameLine();
                if (g.Button("Remove saved key"))
                {
                    AssistantKeyStore.Delete(_folder);
                    _keyInput = "";
                    _notice = "The saved key was removed.";
                }
            }
        }

        if (_notice.Length > 0) g.TextWrapped(_notice, Gui.DimColor);
        g.Separator();

        int modelIndex = Array.IndexOf(AssistantClient.Models, _settings.Model);
        if (g.Combo("Model##model", modelIndex, AssistantClient.Models, out int picked))
        {
            _settings.Model = AssistantClient.Models[picked];
            SaveSettings();
        }
        g.TextWrapped("Larger models answer better but cost more per question.", Gui.DimColor);
        g.Separator();

        if (g.Button("Turn off the assistant"))
        {
            _settings.ConsentVersion = 0;
            SaveSettings();
            _showSettings = false;
            _agree = false;
            ClearConversation();
        }
        g.SameLine();
        if (existing is not null && g.Button("Back to chat", highlighted: true)) _showSettings = false;
    }

    // ---- the chat -------------------------------------------------------------------------------------------

    private void DrawChat(Gui g, AssistantContext context, bool typing)
    {
        bool busy = _pending is not null;

        g.Text($"Model: {_settings.Model}", Gui.DimColor);
        g.SameLine();
        if (g.Button("Settings")) { _showSettings = true; _notice = ""; }
        g.SameLine();
        if (g.Button("Clear", enabled: _log.Count > 0 && !busy)) ClearConversation();

        g.BeginChild("chat", 0f, -232f);
        if (_log.Count == 0)
        {
            g.TextWrapped("Ask about your game or your scripts. Tick the boxes below to include the open script or the selected " +
                          "object; otherwise only your question is sent.", Gui.DimColor);
        }
        for (int i = 0; i < _log.Count; i++)
            DrawEntry(g, _log[i], i);
        if (busy) g.TextDisabled("Thinking" + new string('.', 1 + (int)(Environment.TickCount64 / 500 % 3)));
        if (_stickToEnd > 0)
        {
            g.ScrollChildToEnd("chat");
            _stickToEnd--;
        }
        g.EndChild();

        // What will be attached to the next question.
        bool includeScript = _settings.IncludeScript, includeSelection = _settings.IncludeSelection;
        if (context.ScriptText is { } script)
        {
            g.Text($"Open script: {SafeLabel.Of(context.ScriptName)} ({script.Length:N0} characters)");
            if (g.Checkbox("Send it##script", ref includeScript))
                _settings.IncludeScript = includeScript;
        }
        else
        {
            g.TextDisabled("No script is open to include.");
        }
        if (context.SelectionSummary is { } selection)
        {
            g.Text($"Selected object: {SafeLabel.Of(context.SelectionName)} ({selection.Length:N0} characters)");
            if (g.Checkbox("Send it##selection", ref includeSelection))
                _settings.IncludeSelection = includeSelection;
        }
        else
        {
            g.TextDisabled("No object is selected to include.");
        }
        if (includeScript != _settings.IncludeScript || includeSelection != _settings.IncludeSelection) SaveSettings();

        g.InputText("##prompt", ref _prompt, AssistantPrompt.MaxQuestionChars);
        bool submitted = !busy && _wasTyping && g.EnterPressed && _prompt.Trim().Length > 0;
        if (g.Button("Send", highlighted: true, enabled: !busy && _prompt.Trim().Length > 0) || submitted)
        {
            Send(context);
            g.FocusNextInput();
        }
        g.SameLine();
        if (g.Button("Stop", enabled: busy)) _cts?.Cancel();

        g.TextWrapped("Sent to Anthropic only when you press Send. AI answers can be wrong: check them. Not affiliated with Anthropic.", Gui.DimColor);
    }

    private void DrawEntry(Gui g, Entry entry, int index)
    {
        g.PushId("entry" + index);
        switch (entry.Role)
        {
            case "user":
                g.Text("You", UserColor);
                g.TextWrapped(AssistantText.ToAscii(entry.Text));
                if (entry.Note is not null) g.TextDisabled(entry.Note);
                break;
            case "assistant":
                g.Text("Assistant", AssistantColor);
                g.TextWrapped(AssistantText.ToAscii(entry.Text));
                if (entry.Note is not null) g.TextDisabled(entry.Note);
                if (g.Button("Copy answer")) Copy(entry.Text);
                var blocks = AssistantText.CodeBlocks(entry.Text);
                for (int b = 0; b < blocks.Count; b++)
                {
                    g.SameLine();
                    if (g.Button(blocks.Count == 1 ? "Copy code" : $"Copy code {b + 1}##code{b}")) Copy(blocks[b]);
                }
                break;
            default:
                g.TextWrapped(AssistantText.ToAscii(entry.Text), Gui.ErrorColor);
                break;
        }
        g.Spacing();
        g.PopId();
    }

    // ---- sending ----------------------------------------------------------------------------------------------

    private void Send(AssistantContext context)
    {
        if (_pending is not null) return;
        string question = _prompt.Trim();
        string? key = AssistantKeyStore.Resolve(out _, _folder);
        if (question.Length == 0) return;
        if (AssistantKeyStore.ValidateKey(key) is { } problem)
        {
            _log.Add(new Entry("error", problem, null));
            return;
        }

        string text = AssistantPrompt.BuildUserMessage(question, context, _settings.IncludeScript, _settings.IncludeSelection);
        var attached = new List<string>();
        if (_settings.IncludeScript && context.ScriptText is not null) attached.Add("the open script");
        if (_settings.IncludeSelection && context.SelectionSummary is not null) attached.Add("the selected object");

        _conversation.Add(new ChatMessage("user", text));
        _log.Add(new Entry("user", question, attached.Count > 0 ? "Sent with " + string.Join(" and ", attached) + "." : null));
        _lastQuestion = question;
        _prompt = "";
        _stickToEnd = 3;

        var messages = AssistantPrompt.Recent(_conversation);
        string model = _settings.Model;
        var cts = _cts = new CancellationTokenSource();
        _pending = Task.Run(() => AssistantClient.SendAsync(Http, key!, model, AssistantPrompt.System, messages,
            AssistantClient.DefaultMaxTokens, cts.Token), cts.Token);
    }

    // Picks up the answer when it arrives.
    private void Poll()
    {
        if (_pending is not { IsCompleted: true } done) return;
        _pending = null;
        _cts?.Dispose();
        _cts = null;
        _stickToEnd = 3;

        if (done.IsCanceled || (done.IsFaulted && done.Exception?.GetBaseException() is OperationCanceledException))
        {
            Undo("Stopped. Nothing more was sent.");
        }
        else if (done.IsFaulted)
        {
            var error = done.Exception!.GetBaseException();
            Undo(error is AssistantException ? error.Message : "Something went wrong talking to Anthropic. Try again.");
        }
        else
        {
            var reply = done.Result;
            _conversation.Add(new ChatMessage("assistant", reply.Text));
            string note = $"{reply.InputTokens:N0} tokens in, {reply.OutputTokens:N0} tokens out" +
                          (reply.StopReason == "max_tokens" ? ". The answer was cut off at the length limit." : ".");
            _log.Add(new Entry("assistant", reply.Text, note));
        }
    }

    // A question that got no answer is taken back, so the conversation stays in step and the text can be sent again.
    private void Undo(string message)
    {
        if (_conversation.Count > 0 && _conversation[^1].Role == "user") _conversation.RemoveAt(_conversation.Count - 1);
        _log.Add(new Entry("error", message, null));
        if (_prompt.Length == 0) _prompt = _lastQuestion;
    }

    private void ClearConversation()
    {
        _cts?.Cancel();
        _log.Clear();
        _conversation.Clear();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private void SaveSettings()
    {
        try { _settings.Save(_folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _notice = "Couldn't save the assistant's settings: " + ex.Message; }
    }

    private static void Copy(string text) => ImGui.SetClipboardText(text.ToU8());

    // Opens one of the fixed Anthropic pages in the browser.
    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://www.anthropic.com/") && !url.StartsWith("https://console.anthropic.com/")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
