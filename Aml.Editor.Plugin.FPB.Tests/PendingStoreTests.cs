// B25: diagram changes that were not applied survive opening another file in
// the editor. The editor unloads the document before it tells the plugin, so
// the plugin keeps them on disk, per file and process view, and offers them
// when the same file is opened again.

using Aml.Editor.Plugin.FPB.Views;
using Xunit;

namespace Aml.Editor.Plugin.FPB.Tests;

public sealed class PendingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpb-pending-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static PendingEntry Entry(string file = "Showcase.aml", string ih = "{IH-1}", string snapshot = "[{\"process\":1}]") =>
        new(file, ih, "FPD_Heating", new DateTime(2026, 11, 10, 21, 40, 0), "1A2B3C4D", snapshot);

    [Fact]
    public void Keeps_an_entry_until_it_is_removed()
    {
        var store = new PendingStore(_dir);
        store.Save(Entry());

        var found = store.Find("Showcase.aml", "{IH-1}");

        Assert.NotNull(found);
        Assert.Equal("[{\"process\":1}]", found!.Snapshot);
        Assert.Equal("FPD_Heating", found.IhLabel);
        Assert.Equal(new DateTime(2026, 11, 10, 21, 40, 0), found.SavedAt);
        Assert.Equal("1A2B3C4D", found.IhHash);

        store.Remove("Showcase.aml", "{IH-1}");
        Assert.Null(store.Find("Showcase.aml", "{IH-1}"));
    }

    [Fact]
    public void Finds_the_file_by_its_name_whatever_the_path_and_case()
    {
        var store = new PendingStore(_dir);
        store.Save(Entry(file: @"C:\Models\Showcase.aml", ih: "{ih-1}"));

        Assert.NotNull(store.Find("showcase.AML", "IH-1"));
        Assert.NotNull(store.Find(@"D:\elsewhere\Showcase.aml", "{IH-1}"));
    }

    [Fact]
    public void Keeps_two_process_views_of_one_file_apart()
    {
        var store = new PendingStore(_dir);
        store.Save(Entry(ih: "{A}", snapshot: "a"));
        store.Save(Entry(ih: "{B}", snapshot: "b"));

        Assert.Equal("a", store.Find("Showcase.aml", "{A}")!.Snapshot);
        Assert.Equal("b", store.Find("Showcase.aml", "{B}")!.Snapshot);
    }

    [Fact]
    public void A_newer_save_replaces_the_older_one()
    {
        var store = new PendingStore(_dir);
        store.Save(Entry(snapshot: "old"));
        store.Save(Entry(snapshot: "new"));

        Assert.Equal("new", store.Find("Showcase.aml", "{IH-1}")!.Snapshot);
    }

    [Fact]
    public void Nothing_saved_and_a_broken_file_both_find_nothing()
    {
        var store = new PendingStore(_dir);
        Assert.Null(store.Find("Showcase.aml", "{IH-1}"));

        store.Save(Entry());
        foreach (var file in Directory.GetFiles(_dir)) File.WriteAllText(file, "{ not json");

        Assert.Null(store.Find("Showcase.aml", "{IH-1}"));
    }

    [Fact]
    public void Without_a_file_name_nothing_is_kept()
    {
        var store = new PendingStore(_dir);
        store.Save(Entry(file: ""));

        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
        Assert.Null(store.Find("", "{IH-1}"));
    }
}
