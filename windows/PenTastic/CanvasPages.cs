using System.Text.Json;

namespace PenTastic;

sealed class CanvasPages
{
    public sealed class Page(string id, CanvasDocument doc, Paper paper)
    {
        public string Id { get; } = id;
        public CanvasDocument Doc { get; } = doc;
        public Paper Paper { get; set; } = paper;
    }

    sealed record Index(int Current, List<IndexEntry> Pages);
    sealed record IndexEntry(string Id, Paper Paper);

    readonly string _dir;
    readonly double _width, _height;

    public List<Page> Pages { get; } = [];
    public int Current { get; private set; }
    public Page CurrentPage => Pages[Current];

    public event Action? Changed;

    public CanvasPages(string dir, double width, double height)
    {
        (_dir, _width, _height) = (dir, width, height);
        try
        {
            var index = JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath));
            foreach (var e in index?.Pages ?? [])
            {
                var path = PagePath(e.Id);
                if (!File.Exists(path)) continue;
                using var file = File.OpenRead(path);
                Pages.Add(Watch(new Page(e.Id, CanvasDocument.Load(file, width, height), e.Paper)));
            }
            Current = Math.Clamp(index?.Current ?? 0, 0, Math.Max(0, Pages.Count - 1));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            if (e is not FileNotFoundException and not DirectoryNotFoundException) Log.Write($"canvas pages: {e.Message}");
        }
        if (Pages.Count == 0) Pages.Add(NewPage(Paper.White));
    }

    string IndexPath => Path.Combine(_dir, "pages.json");
    string PagePath(string id) => Path.Combine(_dir, $"{id}.isf");

    Page NewPage(Paper paper) => Watch(new Page(Guid.NewGuid().ToString("N"), new CanvasDocument(_width, _height), paper));

    Page Watch(Page p)
    {
        p.Doc.Changed += () => Changed?.Invoke();
        return p;
    }

    public void Go(int index)
    {
        index = Math.Clamp(index, 0, Pages.Count - 1);
        if (index == Current) return;
        CurrentPage.Doc.End();
        Current = index;
        Changed?.Invoke();
    }

    public void Add()
    {
        CurrentPage.Doc.End();
        Pages.Insert(Current + 1, NewPage(CurrentPage.Paper));
        Current++;
        Changed?.Invoke();
    }

    public void Delete()
    {
        if (Pages.Count == 1) Pages[0] = NewPage(CurrentPage.Paper);
        else
        {
            Pages.RemoveAt(Current);
            Current = Math.Min(Current, Pages.Count - 1);
        }
        Changed?.Invoke();
    }

    public void SetPaper(Paper paper)
    {
        if (CurrentPage.Paper == paper) return;
        CurrentPage.Paper = paper;
        Changed?.Invoke();
    }

    public void Save()
    {
        Directory.CreateDirectory(_dir);
        foreach (var p in Pages)
        {
            using (var file = File.Create(PagePath(p.Id) + ".tmp")) p.Doc.Save(file);
            File.Move(PagePath(p.Id) + ".tmp", PagePath(p.Id), overwrite: true);
        }
        var index = new Index(Current, Pages.Select(p => new IndexEntry(p.Id, p.Paper)).ToList());
        File.WriteAllText(IndexPath + ".tmp", JsonSerializer.Serialize(index));
        File.Move(IndexPath + ".tmp", IndexPath, overwrite: true);
        var keep = Pages.Select(p => PagePath(p.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in Directory.EnumerateFiles(_dir, "*.isf").Where(f => !keep.Contains(f))) File.Delete(old);
    }
}
