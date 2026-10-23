using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GuitarCam.Projects;

namespace GuitarCam.App.Controls;

/// <summary>
/// The "Proyectos" manager. Same as SessionPicker in the web app (GuitarCam/src/components/SessionPicker.tsx):
/// list with search, new project with a template, rename (F2), save as template, duplicate, show in
/// Explorer, delete to the Recycle Bin, forget missing ones, projects folder. Differences, all from the
/// native format: it lists guitarcam.json projects, and "Abrir carpeta…" on a web project offers an import.
/// The window wires the callbacks; this control never touches the open project directly.
/// </summary>
public sealed partial class ProjectsDialog : UserControl
{
    enum DraftKind { New, Rename, Template }

    sealed record Draft(DraftKind Kind, string Name, string? Dir);

    List<ProjectLibrary.Summary> projects = [];
    string root = "";
    bool loaded;
    string? busy;
    string? error, notice;
    Draft? draft;
    string? confirmDelete;
    string? highlight;
    string? pendingWebDir;
    List<ProjectTemplate> myTemplates = ProjectTemplates.User();
    string templateId = ProjectTemplates.LastId;
    /// <summary>"Video" / "Solo audio" of a new project (native only), remembered like the template.</summary>
    string newMode = AppConfig.Get("newProjectMode") == "audio" ? "audio" : "video";
    TextBox? rowInput;
    int previewVersion;

    /// <summary>Folder of the open project, if any.</summary>
    public string? CurrentDir { get; set; }

    /// <summary>Opens a project (and closes the manager). Throws to show the error here.</summary>
    public Func<string, Task>? OpenProject { get; set; }

    /// <summary>Imports a web project and opens the copy.</summary>
    public Func<string, Task>? ImportWebProject { get; set; }

    /// <summary>Writes what's pending of the open project (before duplicating it).</summary>
    public Func<Task>? FlushCurrent { get; set; }

    /// <summary>Closes the open project so its folder can be renamed; the window reopens it at the returned dir.</summary>
    public Func<Task>? CloseCurrent { get; set; }

    public Func<string, Task>? ReopenCurrent { get; set; }

    /// <summary>The open project as a template, for "Guardar como plantilla".</summary>
    public Func<string, ProjectTemplate>? CurrentTemplate { get; set; }

    /// <summary>The manager was dismissed with a project open (Cerrar or Esc).</summary>
    public event Action? Dismissed;

    bool CanCancel => CurrentDir != null;

    public ProjectsDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Cancel();
        NewButton.Click += (_, _) => StartNew();
        OpenFolderButton.Click += async (_, _) => await OpenFolder();
        ImportWebButton.Click += async (_, _) =>
        {
            if (pendingWebDir is not { } dir) return;
            await Run("__import", async () =>
            {
                if (ImportWebProject != null) await ImportWebProject(dir);
            }, "No se pudo importar el proyecto");
        };
        SearchBox.TextChanged += (_, _) => RenderList();
        NewCancel.Click += (_, _) => SetDraft(null);
        NewCreate.Click += async (_, _) => await SubmitDraft();
        NewVideoButton.Click += (_, _) => { newMode = "video"; RenderAll(); };
        NewAudioButton.Click += (_, _) => { newMode = "audio"; RenderAll(); };
        NewName.TextChanged += (_, _) =>
        {
            if (draft?.Kind == DraftKind.New) draft = draft with { Name = NewName.Text ?? "" };
            RenderNewForm(false);
            _ = PreviewNewName();
        };
        NewName.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitDraft();
            }
        };
        RevealRoot.Click += (_, _) => Reveal(root);
        ChangeRoot.Click += async (_, _) => await PickRoot();
        SizeChanged += (_, _) => Layout();
    }

    void Layout()
    {
        // anchored near the top (not centered) so rows don't jump when a form or notice appears
        double h = Bounds.Height;
        Card.Margin = new Thickness(16, Math.Max(16, h * 0.1), 16, 16);
        Card.MaxHeight = Math.Max(200, Math.Min(720, h - 32));
        Card.Width = Math.Min(672, Math.Max(0, Bounds.Width - 32));
    }

    /// <summary>Shows the manager and reloads the list.</summary>
    public async Task Show(string? currentDir)
    {
        CurrentDir = currentDir;
        IsVisible = true;
        CloseButton.IsVisible = CanCancel;
        draft = null;
        confirmDelete = null;
        error = notice = null;
        highlight = null;
        pendingWebDir = null;
        SearchBox.Text = "";
        myTemplates = ProjectTemplates.User();
        Layout();
        RenderAll();
        await Refresh();
        Focus();
    }

    public void Hide() => IsVisible = false;

    /// <summary>Shows take-loading progress while a manager operation owns the screen.</summary>
    public void ReportProgress(string text)
    {
        if (!IsVisible || busy == null) return;
        NoticeText.Text = text;
        NoticeText.Foreground = Res("Dim");
        NoticeText.IsVisible = true;
        NoticePanel.IsVisible = true;
    }

    void Cancel()
    {
        if (!CanCancel || busy != null) return;
        Hide();
        Dismissed?.Invoke();
    }

    /// <summary>Esc closes an inline form first, then the manager. Returns true when it used the key.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return false;
        if (draft != null || confirmDelete != null)
        {
            draft = null;
            confirmDelete = null;
            RenderAll();
        }
        else Cancel();
        return true;
    }

    async Task<List<ProjectLibrary.Summary>?> Refresh()
    {
        try
        {
            var (r, list) = await Task.Run(ProjectLibrary.List);
            root = r;
            projects = list;
            return list;
        }
        catch (Exception e)
        {
            error = $"No se pudo leer la carpeta de proyectos: {e.Message}";
            return null;
        }
        finally
        {
            loaded = true;
            RenderAll();
        }
    }

    async Task Run(string key, Func<Task> fn, string fallback)
    {
        if (busy != null) return;
        busy = key;
        error = notice = null;
        RenderAll();
        try
        {
            await fn();
        }
        catch (Exception e)
        {
            error = string.IsNullOrWhiteSpace(e.Message) ? fallback : e.Message;
        }
        finally
        {
            busy = null;
            RenderAll();
            if (IsVisible && draft == null) Focus();
        }
    }

    // --- names (same rules as the web: the client warns, the library makes the folder unique) ------

    static bool Same(string? a, string? b) => ProjectLibrary.SamePath(a, b);

    string NextDefaultName()
    {
        var used = projects.Select(p => p.Name.ToLowerInvariant()).ToHashSet();
        for (int i = projects.Count + 1; ; i++)
            if (!used.Contains($"proyecto {i}")) return $"Proyecto {i}";
    }

    bool NameTaken(string name, string? exceptDir = null) =>
        projects.Any(p => p.Exists && !Same(p.Dir, exceptDir) && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    string FreeName(string name)
    {
        string b = name.Trim();
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? b : $"{b} ({i})";
            if (!NameTaken(candidate)) return candidate;
        }
    }

    ProjectTemplate? SelectedTemplate =>
        ProjectTemplates.Builtin.Concat(myTemplates).FirstOrDefault(t => t.Id == templateId);

    // --- actions ----------------------------------------------------------------------------------

    void SetDraft(Draft? d)
    {
        draft = d;
        pendingWebDir = null;
        if (d != null) confirmDelete = null;
        RenderAll();
        if (d?.Kind == DraftKind.New)
        {
            NewName.Text = d.Name;
            _ = PreviewNewName();
            Dispatcher.UIThread.Post(() =>
            {
                NewName.Focus();
                NewName.SelectAll();
            });
        }
        else if (d != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                rowInput?.Focus();
                rowInput?.SelectAll();
            });
        }
        else Focus();
    }

    void StartNew() => SetDraft(new Draft(DraftKind.New, NextDefaultName(), null));

    /// <summary>Checks disk collisions off the UI thread; older name checks never replace the latest draft.</summary>
    async Task PreviewNewName()
    {
        int version = ++previewVersion;
        var d = draft;
        if (d?.Kind != DraftKind.New || d.Name.Trim().Length == 0 || d.Dir != null) return;
        try
        {
            await Task.Delay(150);
            if (version != previewVersion) return;
            var result = await Task.Run(() => ProjectLibrary.PreviewNew(d.Name));
            if (version != previewVersion || draft?.Kind != DraftKind.New || draft.Name != d.Name) return;
            NewPath.Text = result.Dir;
            NewTaken.IsVisible = result.Name != d.Name.Trim();
            NewTaken.Text = $"Ya hay un proyecto con ese nombre: este se va a llamar \"{result.Name}\".";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Creation reports the actionable error; a path preview must not interrupt typing.
        }
    }

    async Task Open(ProjectLibrary.Summary p)
    {
        if (Same(p.Dir, CurrentDir))
        {
            Cancel();
            return;
        }
        await Run(p.Dir, async () =>
        {
            if (OpenProject != null) await OpenProject(p.Dir);
        }, "No se pudo abrir el proyecto");
    }

    async Task SubmitDraft()
    {
        if (draft == null || busy != null) return;
        string name = draft.Name.Trim();
        if (name.Length == 0) return;
        var d = draft;
        if (d.Kind == DraftKind.New)
        {
            var tpl = SelectedTemplate;
            string mode = newMode;
            await Run("__create", async () =>
            {
                string freeName = FreeName(name);
                string dir = await Task.Run(() => d.Dir != null
                    ? ProjectLibrary.Create(freeName, tpl, d.Dir, mode)
                    : ProjectLibrary.Create(name, tpl, mode: mode));
                ProjectTemplates.LastId = tpl?.Id ?? ProjectTemplates.None;
                AppConfig.Set("newProjectMode", mode);
                draft = null;
                if (OpenProject != null) await OpenProject(dir);
            }, "No se pudo crear el proyecto");
            return;
        }
        if (d.Kind == DraftKind.Template)
        {
            if (CurrentTemplate == null) return;
            bool replaced = myTemplates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            try
            {
                myTemplates = ProjectTemplates.SaveUser(CurrentTemplate(name));
                templateId = myTemplates.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Id ?? templateId;
                draft = null;
                error = null;
                notice = replaced
                    ? $"Plantilla \"{name}\" actualizada."
                    : $"Plantilla \"{name}\" guardada. La vas a ver al crear un proyecto nuevo.";
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            RenderAll();
            return;
        }
        var target = projects.Find(p => p.Dir == d.Dir);
        if (target == null || name == target.Name)
        {
            SetDraft(null);
            return;
        }
        if (NameTaken(name, d.Dir)) return;
        bool isCurrent = Same(d.Dir, CurrentDir);
        await Run(d.Dir!, async () =>
        {
            string dir = d.Dir!;
            bool reopened = false;
            if (isCurrent && CloseCurrent != null) await CloseCurrent();
            try
            {
                var res = await Task.Run(() => ProjectLibrary.Rename(dir, name, true));
                dir = res.Dir;
                if (res.FolderKept) notice = "El nombre cambió, pero la carpeta no se pudo renombrar (¿hay un archivo abierto adentro?).";
                draft = null;
                highlight = res.Dir;
                if (isCurrent && ReopenCurrent != null)
                {
                    CurrentDir = res.Dir;
                    await ReopenCurrent(res.Dir);
                    reopened = true;
                }
                await Refresh();
            }
            finally
            {
                if (isCurrent && !reopened && ReopenCurrent != null) await ReopenCurrent(dir);
            }
        }, "No se pudo renombrar el proyecto");
    }

    async Task Duplicate(ProjectLibrary.Summary p)
    {
        bool isCurrent = Same(p.Dir, CurrentDir);
        await Run($"dup:{p.Dir}", async () =>
        {
            if (isCurrent && FlushCurrent != null) await FlushCurrent();
            string dir = await Task.Run(() => ProjectLibrary.Duplicate(p.Dir));
            var list = await Refresh();
            var copy = list?.Find(x => Same(x.Dir, dir));
            if (copy != null)
            {
                highlight = copy.Dir;
                SetDraft(new Draft(DraftKind.Rename, copy.Name, copy.Dir));
            }
        }, "No se pudo duplicar el proyecto");
    }

    async Task Remove(ProjectLibrary.Summary p) =>
        await Run($"del:{p.Dir}", async () =>
        {
            if (Same(p.Dir, CurrentDir)) throw new IOException("Abrí otro proyecto para poder borrar este");
            await Task.Run(() => ProjectLibrary.Delete(p.Dir));
            confirmDelete = null;
            notice = $"\"{p.Name}\" se mandó a la Papelera de reciclaje.";
            await Refresh();
        }, "No se pudo borrar el proyecto");

    async Task Forget(ProjectLibrary.Summary p) =>
        await Run($"forget:{p.Dir}", async () =>
        {
            await Task.Run(() => ProjectLibrary.Forget(p.Dir));
            await Refresh();
        }, "No se pudo quitar de la lista");

    async Task<string?> PickFolder(string title)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null) return null;
        var start = Directory.Exists(root) ? await sp.TryGetFolderFromPathAsync(root) : null;
        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = start });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    async Task OpenFolder() =>
        await Run("__pick", async () =>
        {
            var dir = await PickFolder("Elegí la carpeta de un proyecto (o una vacía para uno nuevo)");
            if (dir == null) return;
            if (ProjectStore.IsProject(dir))
            {
                if (OpenProject != null) await OpenProject(dir);
            }
            else if (WebImporter.IsWebProject(dir))
            {
                pendingWebDir = dir;
                notice = "Es un proyecto de la app web: podés importarlo a una carpeta nueva. El original queda igual.";
            }
            else
            {
                if (await Task.Run(() => Directory.EnumerateFileSystemEntries(dir).Any()))
                    throw new IOException("Esa carpeta no está vacía. Elegí una carpeta vacía para crear el proyecto.");
                busy = null;
                SetDraft(new Draft(DraftKind.New, System.IO.Path.GetFileName(dir.TrimEnd('\\')) is { Length: > 0 } n ? n : NextDefaultName(), dir));
            }
        }, "No se pudo abrir el selector de carpetas");

    async Task PickRoot() =>
        await Run("__root", async () =>
        {
            var dir = await PickFolder("Elegí dónde guardar los proyectos nuevos");
            if (dir == null) return;
            await Task.Run(() => ProjectLibrary.SetRoot(dir));
            await Refresh();
        }, "No se pudo cambiar la carpeta de proyectos");

    void RemoveTemplate(ProjectTemplate t)
    {
        myTemplates = ProjectTemplates.DeleteUser(t.Id);
        if (templateId == t.Id) templateId = ProjectTemplates.None;
        notice = $"Se borró la plantilla \"{t.Name}\".";
        RenderAll();
    }

    static void Reveal(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        Explorer.Open(dir);
    }

    // --- rendering --------------------------------------------------------------------------------

    void RenderAll()
    {
        bool idle = busy == null;
        NewButton.IsEnabled = idle;
        OpenFolderButton.IsEnabled = idle;
        ChangeRoot.IsEnabled = idle;
        RevealRoot.IsEnabled = root.Length > 0;
        bool picking = busy == "__pick";
        OpenFolderIcon.IsVisible = !picking;
        OpenFolderSpin.IsVisible = picking;
        OpenFolderText.Text = picking ? "Esperando a Windows…" : "Abrir carpeta…";
        RootSpin.IsVisible = busy == "__root";
        ImportWebButton.IsVisible = pendingWebDir != null;
        ImportWebButton.IsEnabled = idle;
        RootText.Text = root.Length > 0 ? root : "…";
        ToolTip.SetTip(RootText, root);
        RenderNewForm(true);
        NoticeText.IsVisible = error != null || notice != null;
        NoticePanel.IsVisible = NoticeText.IsVisible || pendingWebDir != null;
        NoticeText.Text = error ?? notice;
        NoticeText.Foreground = error != null ? (IBrush)this.FindResource("Rec")! : (IBrush)this.FindResource("Dim")!;
        RenderList();
    }

    void RenderNewForm(bool templates)
    {
        var d = draft?.Kind == DraftKind.New ? draft : null;
        NewForm.IsVisible = d != null;
        if (d == null) return;
        string name = d.Name.Trim();
        NewCreate.IsEnabled = busy == null && name.Length > 0;
        NewVideoButton.IsChecked = newMode == "video";
        NewAudioButton.IsChecked = newMode == "audio";
        CreateSpin.IsVisible = busy == "__create";
        NewPath.Text = d.Dir ?? (root.Length > 0 ? System.IO.Path.Combine(root, ProjectLibrary.FolderNameFor(FreeName(d.Name))) : "");
        bool taken = name.Length > 0 && NameTaken(name);
        NewTaken.IsVisible = taken;
        if (taken) NewTaken.Text = $"Ya hay un proyecto con ese nombre: este se va a llamar \"{FreeName(d.Name)}\".";
        if (!templates) return;
        TemplateGrid.Children.Clear();
        var selected = SelectedTemplate;
        TemplateGrid.Children.Add(TemplateOption("Vacío", "Sin pistas", null, selected == null, false, () => templateId = ProjectTemplates.None, null));
        foreach (var t in ProjectTemplates.Builtin.Concat(myTemplates))
            TemplateGrid.Children.Add(TemplateOption(t.Name, t.Summary, t.Hint, selected?.Id == t.Id, !t.Builtin, () => templateId = t.Id,
                t.Builtin ? null : () => RemoveTemplate(t)));
        _ = PreviewNewName();
    }

    Control TemplateOption(string name, string detail, string? hint, bool selected, bool mine, Action onSelect, Action? onDelete)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (mine)
            title.Children.Add(new TextBlock { Text = "MÍA", FontSize = 9, LetterSpacing = 0.5, Foreground = Res("Faint"), VerticalAlignment = VerticalAlignment.Center });
        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = Res("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        var main = new Button { Classes = { "tplmain" }, Content = body };
        if (onDelete != null) main.Padding = new Thickness(10, 6, 32, 6);
        ToolTip.SetTip(main, hint != null ? $"{hint}\n{detail}" : detail);
        main.Click += (_, _) =>
        {
            onSelect();
            RenderNewForm(true);
        };
        var grid = new Grid();
        grid.Children.Add(main);
        if (onDelete != null)
        {
            var del = new Button
            {
                Classes = { "act", "danger", "tpldel" },
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Content = Icon("", 11),
            };
            ToolTip.SetTip(del, "Borrar plantilla");
            del.Click += (_, _) => onDelete();
            grid.Children.Add(del);
        }
        var b = new Border { Classes = { "tpl" }, Child = grid, Margin = new Thickness(0, 0, 6, 6) };
        if (selected) b.Classes.Add("selected");
        return b;
    }

    void RenderList()
    {
        ListPanel.Children.Clear();
        rowInput = null;
        if (!loaded)
        {
            ListPanel.Children.Add(new Arc
            {
                Classes = { "spin" },
                Width = 20,
                Height = 20,
                Stroke = Res("Faint"),
                Margin = new Thickness(0, 32),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }
        string q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        var visible = q.Length == 0
            ? projects
            : projects.Where(p => p.Name.ToLowerInvariant().Contains(q) || p.Dir.ToLowerInvariant().Contains(q)).ToList();
        if (visible.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = projects.Count == 0
                    ? "Todavía no tenés proyectos. Creá uno con \"Nuevo proyecto\"."
                    : "Ningún proyecto coincide con la búsqueda.",
                FontSize = 12,
                Foreground = Res("Faint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(24, 32),
            });
            return;
        }
        foreach (var p in visible) ListPanel.Children.Add(Row(p));
    }

    Control Row(ProjectLibrary.Summary p)
    {
        bool isCurrent = Same(p.Dir, CurrentDir);
        var renaming = draft != null && draft.Kind != DraftKind.New && draft.Dir == p.Dir ? draft : null;
        bool deleting = confirmDelete == p.Dir;
        bool rowBusy = busy != null && busy.EndsWith(p.Dir);
        var row = new Border { Classes = { "row" } };
        if (highlight != null && Same(highlight, p.Dir)) row.Classes.Add("highlight");
        if (deleting) row.Classes.Add("deleting");

        if (renaming != null)
        {
            row.Child = RenameRow(p, renaming, rowBusy);
            return row;
        }
        if (deleting)
        {
            row.Child = DeleteRow(p, rowBusy);
            return row;
        }
        row.Classes.Add("live");

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = p.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        if (isCurrent)
            title.Children.Add(new Border
            {
                Background = Res("AccentSoft"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "ABIERTO", FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.5, Foreground = Res("Accent") },
            });
        var info = new StackPanel { Opacity = p.Exists ? 1 : 0.5 };
        info.Children.Add(title);
        info.Children.Add(new TextBlock
        {
            Text = p.Exists ? Meta(p) : "No se encontró la carpeta",
            FontSize = 11,
            Foreground = Res("Faint"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (!p.InRoot)
            info.Children.Add(new TextBlock { Text = p.Dir, Classes = { "mono" }, FontSize = 10, Foreground = Res("Faint"), Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });

        var main = new Button { Classes = { "rowmain" }, Content = info, IsEnabled = p.Exists && busy == null };
        ToolTip.SetTip(main, p.Dir);
        main.Click += async (_, _) => await Open(p);
        main.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && p.Exists)
            {
                e.Handled = true;
                SetDraft(new Draft(DraftKind.Rename, p.Name, p.Dir));
            }
        };

        var right = new Panel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        if (rowBusy) right.Children.Add(Spinner(16, Res("Faint")));
        else
        {
            right.Children.Add(new TextBlock
            {
                Classes = { "ago" },
                Text = p.LastTouched is { } t ? TimeAgo(t) : "",
                FontSize = 11,
                Foreground = Res("Faint"),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            var actions = new StackPanel { Classes = { "actions" }, Orientation = Orientation.Horizontal, Spacing = 2 };
            if (p.Exists)
            {
                actions.Children.Add(RowAction("", "Renombrar (F2)", () => SetDraft(new Draft(DraftKind.Rename, p.Name, p.Dir))));
                if (isCurrent && CurrentTemplate != null)
                    actions.Children.Add(RowAction("", "Guardar como plantilla (pistas, canales, efectos, formato y color)",
                        () => SetDraft(new Draft(DraftKind.Template, p.Name, p.Dir))));
                actions.Children.Add(RowAction("", "Duplicar", async () => await Duplicate(p)));
                actions.Children.Add(RowAction("", "Mostrar en el Explorador", () => Reveal(p.Dir)));
                var del = RowAction("", isCurrent ? "Abrí otro proyecto para poder borrar este" : "Borrar", () =>
                {
                    draft = null;
                    confirmDelete = p.Dir;
                    RenderAll();
                }, danger: true);
                // a disabled button shows no tooltip: keep it enabled-looking off, but inert
                if (isCurrent)
                {
                    del.Opacity = 0.3;
                    del.Classes.Remove("danger");
                    del.Classes.Add("inert");
                }
                actions.Children.Add(del);
            }
            else
            {
                var forget = new Button { Classes = { "btn" }, Height = 28, Padding = new Thickness(10, 0), FontSize = 11, Content = "Quitar de la lista" };
                forget.Click += async (_, _) => await Forget(p);
                actions.Children.Add(forget);
            }
            right.Children.Add(actions);
        }

        var grid = new Grid();
        grid.Children.Add(main);
        grid.Children.Add(right);
        row.Child = grid;
        return row;
    }

    Control RenameRow(ProjectLibrary.Summary p, Draft d, bool rowBusy)
    {
        bool savingTemplate = d.Kind == DraftKind.Template;
        bool templateExists = savingTemplate && myTemplates.Any(t => string.Equals(t.Name, d.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        bool renameTaken = d.Kind == DraftKind.Rename && d.Name.Trim() != p.Name && NameTaken(d.Name, p.Dir);

        var input = new TextBox { Classes = { "field" }, Text = d.Name, MaxLength = 120, Watermark = savingTemplate ? "Nombre de la plantilla" : null };
        AutomationProperties.SetName(input, savingTemplate ? "Nombre de la plantilla" : "Nuevo nombre");
        rowInput = input;
        var submit = new Button
        {
            Classes = { "btn", "primary" },
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = busy == null && d.Name.Trim().Length > 0 && !renameTaken,
        };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (rowBusy) label.Children.Add(Spinner(14, Res("App")));
        label.Children.Add(new TextBlock { Text = savingTemplate ? (templateExists ? "Reemplazar plantilla" : "Guardar plantilla") : "Renombrar" });
        submit.Content = label;
        submit.Click += async (_, _) => await SubmitDraft();

        var error = new TextBlock { Text = "Ya hay otro proyecto con ese nombre.", FontSize = 11, Foreground = Res("Rec"), Margin = new Thickness(16, -4, 16, 10), IsVisible = renameTaken };
        input.TextChanged += (_, _) =>
        {
            if (draft == null) return;
            draft = draft with { Name = input.Text ?? "" };
            bool taken = draft.Kind == DraftKind.Rename && draft.Name.Trim() != p.Name && NameTaken(draft.Name, p.Dir);
            error.IsVisible = taken;
            submit.IsEnabled = busy == null && draft.Name.Trim().Length > 0 && !taken;
            if (savingTemplate)
            {
                bool exists = myTemplates.Any(t => string.Equals(t.Name, draft.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                ((TextBlock)label.Children[^1]).Text = exists ? "Reemplazar plantilla" : "Guardar plantilla";
            }
        };
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitDraft();
            }
        };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(16, 10) };
        form.Children.Add(input);
        if (savingTemplate)
        {
            var cancel = new Button { Classes = { "btn" }, Content = "Cancelar", Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += (_, _) => SetDraft(null);
            Grid.SetColumn(cancel, 1);
            form.Children.Add(cancel);
        }
        Grid.SetColumn(submit, 2);
        form.Children.Add(submit);
        var stack = new StackPanel();
        stack.Children.Add(form);
        stack.Children.Add(error);
        return stack;
    }

    Control DeleteRow(ProjectLibrary.Summary p, bool rowBusy)
    {
        var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run("¿Mandar "));
        text.Inlines.Add(new Avalonia.Controls.Documents.Run($"\"{p.Name}\"") { FontWeight = FontWeight.SemiBold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(" a la Papelera? Se van todas sus tomas y exports."));
        var cancel = new Button { Classes = { "btn", "sm" }, Content = "Cancelar", Margin = new Thickness(12, 0, 0, 0) };
        cancel.Click += (_, _) =>
        {
            confirmDelete = null;
            RenderAll();
        };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(rowBusy ? Spinner(14, Brushes.White) : Icon("", 12));
        label.Children.Add(new TextBlock { Text = "Borrar" });
        var del = new Button { Classes = { "btn", "sm", "danger" }, Content = label, Margin = new Thickness(8, 0, 0, 0), IsEnabled = busy == null };
        del.Click += async (_, _) => await Remove(p);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(16, 10) };
        grid.Children.Add(text);
        Grid.SetColumn(cancel, 1);
        Grid.SetColumn(del, 2);
        grid.Children.Add(cancel);
        grid.Children.Add(del);
        Dispatcher.UIThread.Post(() => del.Focus());
        return grid;
    }

    Button RowAction(string glyph, string title, Action onClick, bool danger = false)
    {
        var b = new Button { Classes = { "act" }, Content = Icon(glyph, 13) };
        if (danger) b.Classes.Add("danger");
        ToolTip.SetTip(b, title);
        b.Click += (_, _) =>
        {
            if (busy != null) return;
            if (b.Classes.Contains("inert")) return;
            if (!danger) confirmDelete = null;
            onClick();
        };
        return b;
    }

    static TextBlock Icon(string glyph, double size) => new() { Classes = { "icon" }, Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center };

    static Arc Spinner(double size, IBrush brush) => new() { Classes = { "spin" }, Width = size, Height = size, Stroke = brush };

    IBrush Res(string key) => (IBrush)this.FindResource(key)!;

    // --- formatting (same as the web) -------------------------------------------------------------

    static readonly string[] Months = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sept", "oct", "nov", "dic"];

    static string TimeAgo(DateTime utc)
    {
        double min = Math.Round((DateTime.UtcNow - utc.ToUniversalTime()).TotalMinutes);
        if (min < 1) return "recién";
        if (min < 60) return $"hace {min} min";
        double h = Math.Round(min / 60);
        if (h < 24) return $"hace {h} h";
        double d = Math.Round(h / 24);
        if (d == 1) return "ayer";
        if (d < 30) return $"hace {d} días";
        var l = utc.ToLocalTime();
        return $"{l.Day} {Months[l.Month - 1]} {l.Year}";
    }

    static string Duration(double sec)
    {
        int s = (int)Math.Round(sec);
        return $"{s / 60}:{s % 60:00}";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{Math.Max(1, Math.Round(bytes / 1024.0))} KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }

    static string Meta(ProjectLibrary.Summary p)
    {
        if (p.TakeCount == 0) return "Vacío";
        var parts = new List<string> { p.TrackCount == 1 ? "1 pista" : $"{p.TrackCount} pistas" };
        if (p.TakeCount != p.TrackCount) parts.Add(p.TakeCount == 1 ? "1 toma" : $"{p.TakeCount} tomas");
        if (p.DurationSec > 0) parts.Add(Duration(p.DurationSec));
        if (p.Bpm is { } bpm and > 0) parts.Add($"{(Math.Round(bpm * 100) / 100).ToString(System.Globalization.CultureInfo.InvariantCulture)} BPM");
        if (p.ExportCount > 0) parts.Add(p.ExportCount == 1 ? "1 export" : $"{p.ExportCount} exports");
        if (p.Bytes > 0) parts.Add(FormatBytes(p.Bytes));
        return string.Join(" · ", parts);
    }
}
