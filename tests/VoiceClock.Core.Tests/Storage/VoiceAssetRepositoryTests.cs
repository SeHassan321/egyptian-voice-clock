using System.Text.Json.Nodes;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Storage;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Storage;

public class VoiceAssetRepositoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StorageLocations _locations;

    public VoiceAssetRepositoryTests()
    {
        _locations = new StorageLocations(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private VoiceAssetRepository NewRepository(IBundledVoicePackSource? bundled = null) =>
        new(_locations, TestCatalog.Catalog, bundled ?? new NoBundledVoicePack());

    [Fact]
    public async Task A_fresh_install_reports_every_clip_missing()
    {
        var repo = NewRepository();
        await repo.InitializeAsync();

        Assert.True(repo.IsInitialized);
        Assert.All(TestCatalog.Catalog.Entries, e => Assert.Equal(AssetState.Missing, repo.GetStatus(e.Id).State));
        Assert.Null(await repo.LoadAsync("hour.01"));
        Assert.False(repo.BundledVoice.Present);
    }

    [Fact]
    public async Task Saved_recording_is_ready_and_loads_back_identically()
    {
        var repo = NewRepository();
        var audio = PackBuilder.ClipFor("hour.01");

        await repo.SaveUserClipAsync("hour.01", audio);

        var status = repo.GetStatus("hour.01");
        Assert.Equal(AssetState.Ready, status.State);
        Assert.Equal(AssetSource.User, status.Source);
        Assert.True(File.Exists(Path.Combine(_locations.UserClipsDirectory, "hour.01.wav")));
        Assert.Equal(audio.Samples, (await repo.LoadAsync("hour.01"))!.Samples);
    }

    [Fact]
    public async Task Recordings_survive_an_app_restart()
    {
        var audio = PackBuilder.ClipFor("minute.43");
        await NewRepository().SaveUserClipAsync("minute.43", audio);

        var afterRestart = NewRepository();
        await afterRestart.InitializeAsync();

        Assert.True(afterRestart.IsAvailable("minute.43"));
        Assert.Equal(audio.Samples, (await afterRestart.LoadAsync("minute.43"))!.Samples);
    }

    [Fact]
    public async Task Manifest_records_id_file_category_and_status()
    {
        await NewRepository().SaveUserClipAsync("minute.15", PackBuilder.ClipFor("minute.15"));

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(_locations.ManifestPath))!;
        var entry = manifest["clips"]!["minute.15"]!;
        Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
        Assert.Equal("user/minute.15.wav", entry["file"]!.GetValue<string>());
        Assert.Equal("RoundMinute", entry["category"]!.GetValue<string>());
        Assert.Equal("recorded", entry["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(entry["sha256"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Replacing_a_recording_keeps_only_the_new_one_and_leaves_no_temp_files()
    {
        var repo = NewRepository();
        await repo.SaveUserClipAsync("hour.02", PackBuilder.ClipFor("first"));
        var second = PackBuilder.ClipFor("second");
        await repo.SaveUserClipAsync("hour.02", second);

        Assert.Equal(second.Samples, (await NewRepository().LoadAsync("hour.02"))!.Samples);
        Assert.Empty(Directory.GetFiles(_locations.VoiceDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Saving_one_clip_never_touches_another()
    {
        var repo = NewRepository();
        var one = PackBuilder.ClipFor("hour.01");
        await repo.SaveUserClipAsync("hour.01", one);
        await repo.SaveUserClipAsync("hour.02", PackBuilder.ClipFor("hour.02"));
        await repo.DeleteUserClipAsync("hour.02");

        Assert.Equal(one.Samples, (await NewRepository().LoadAsync("hour.01"))!.Samples);
    }

    [Fact]
    public async Task An_interrupted_save_does_not_destroy_the_previous_recording()
    {
        var original = PackBuilder.ClipFor("hour.03");
        await NewRepository().SaveUserClipAsync("hour.03", original);

        // Simulate a crash mid-write: a half-written temp file next to the real one.
        var leftover = Path.Combine(_locations.UserClipsDirectory, ".hour.03.wav.deadbeef.tmp");
        await File.WriteAllBytesAsync(leftover, new byte[100]);

        var repo = NewRepository();
        await repo.InitializeAsync();

        Assert.False(File.Exists(leftover));
        Assert.Equal(original.Samples, (await repo.LoadAsync("hour.03"))!.Samples);
    }

    [Fact]
    public async Task Unknown_ids_and_empty_audio_are_rejected()
    {
        var repo = NewRepository();
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveUserClipAsync("hour.13", PackBuilder.ClipFor("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveUserClipAsync("../evil", PackBuilder.ClipFor("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.SaveUserClipAsync("hour.01", new PcmAudio(44100, [])));
    }

    [Fact]
    public async Task Deleting_without_a_default_makes_the_clip_missing()
    {
        var repo = NewRepository();
        await repo.SaveUserClipAsync("hour.04", PackBuilder.ClipFor("hour.04"));

        Assert.True(await repo.DeleteUserClipAsync("hour.04"));
        Assert.Equal(AssetState.Missing, repo.GetStatus("hour.04").State);
        Assert.False(await repo.DeleteUserClipAsync("hour.04"));
        Assert.Equal(AssetState.Missing, NewRepository().GetStatusAfterInit("hour.04"));
    }

    [Fact]
    public async Task Bundled_default_voice_is_used_when_nothing_was_recorded()
    {
        var source = new FakeBundledSource(PackBuilder.Build(["hour.01", "minute.15"]));
        var repo = NewRepository(source);
        await repo.InitializeAsync();

        var status = repo.GetStatus("hour.01");
        Assert.Equal(AssetState.Ready, status.State);
        Assert.Equal(AssetSource.Bundled, status.Source);
        Assert.True(repo.BundledVoice.Present);
        Assert.Equal(2, repo.BundledVoice.ClipCount);
        Assert.NotNull(await repo.LoadAsync("minute.15"));
    }

    [Fact]
    public async Task Own_recording_overrides_the_default_and_deleting_it_restores_the_default()
    {
        var source = new FakeBundledSource(PackBuilder.Build(["hour.05"]));
        var repo = NewRepository(source);
        var own = PackBuilder.ClipFor("my own voice");

        await repo.SaveUserClipAsync("hour.05", own);
        Assert.Equal(AssetSource.User, repo.GetStatus("hour.05").Source);
        Assert.Equal(own.Samples, (await repo.LoadAsync("hour.05"))!.Samples);

        await repo.DeleteUserClipAsync("hour.05");
        var status = repo.GetStatus("hour.05");
        Assert.Equal(AssetSource.Bundled, status.Source);
        Assert.NotEqual(own.SampleCount, (await repo.LoadAsync("hour.05"))!.SampleCount);
    }

    [Fact]
    public async Task Bundled_pack_is_extracted_once_per_app_build()
    {
        var source = new FakeBundledSource(PackBuilder.Build(["hour.06"]));
        await NewRepository(source).InitializeAsync();
        Assert.Equal(1, source.OpenCount);

        var sameBuild = NewRepository(source);
        await sameBuild.InitializeAsync();
        Assert.Equal(1, source.OpenCount);
        Assert.True(sameBuild.IsAvailable("hour.06"));

        source.VersionTag = "1.1 (2)";
        source.Pack = PackBuilder.Build(["hour.07"]);
        var newBuild = NewRepository(source);
        await newBuild.InitializeAsync();
        Assert.Equal(2, source.OpenCount);
        Assert.False(newBuild.IsAvailable("hour.06"));
        Assert.True(newBuild.IsAvailable("hour.07"));
    }

    [Fact]
    public async Task A_broken_bundled_pack_is_reported_not_fatal()
    {
        var repo = NewRepository(new FakeBundledSource([1, 2, 3, 4]));
        await repo.InitializeAsync();

        Assert.False(repo.BundledVoice.Present);
        Assert.NotNull(repo.BundledVoice.Problem);
        Assert.Equal(AssetState.Missing, repo.GetStatus("hour.01").State);
    }

    [Fact]
    public async Task Damaged_own_recording_falls_back_to_the_default()
    {
        var source = new FakeBundledSource(PackBuilder.Build(["hour.08"]));
        await NewRepository(source).SaveUserClipAsync("hour.08", PackBuilder.ClipFor("own"));
        await File.WriteAllBytesAsync(Path.Combine(_locations.UserClipsDirectory, "hour.08.wav"), new byte[300]);

        var repo = NewRepository(source);
        await repo.InitializeAsync();
        var status = repo.GetStatus("hour.08");

        Assert.Equal(AssetState.Ready, status.State);
        Assert.Equal(AssetSource.Bundled, status.Source);
        Assert.NotNull(status.Problem);
        Assert.NotNull(await repo.LoadAsync("hour.08"));
    }

    [Fact]
    public async Task Truncated_recording_is_detected_on_use_and_reported()
    {
        await NewRepository().SaveUserClipAsync("hour.09", PackBuilder.ClipFor("hour.09"));
        var path = Path.Combine(_locations.UserClipsDirectory, "hour.09.wav");
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);

        var repo = NewRepository();
        var changed = new List<string>();
        repo.Changed += (_, e) => changed.AddRange(e.AssetIds);
        await repo.InitializeAsync();

        Assert.Null(await repo.LoadAsync("hour.09"));
        Assert.Equal(AssetState.Invalid, repo.GetStatus("hour.09").State);
        Assert.Contains("hour.09", changed);
    }

    [Fact]
    public async Task Verify_all_finds_damaged_recordings()
    {
        var repo = NewRepository();
        await repo.SaveUserClipAsync("hour.10", PackBuilder.ClipFor("hour.10"));
        await repo.SaveUserClipAsync("hour.11", PackBuilder.ClipFor("hour.11"));
        var path = Path.Combine(_locations.UserClipsDirectory, "hour.11.wav");
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..60]);

        var fresh = NewRepository();
        var result = await fresh.VerifyAllAsync();

        Assert.Equal(2, result.Checked);
        Assert.Equal(new[] { "hour.11" }, result.InvalidIds.ToArray());
        Assert.False(result.AllValid);
    }

    [Fact]
    public async Task Missing_manifest_is_rebuilt_from_the_files()
    {
        await NewRepository().SaveUserClipAsync("hour.12", PackBuilder.ClipFor("hour.12"));
        File.Delete(_locations.ManifestPath);

        var repo = NewRepository();
        await repo.InitializeAsync();

        Assert.True(repo.IsAvailable("hour.12"));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(_locations.ManifestPath))!;
        Assert.NotNull(manifest["clips"]!["hour.12"]);
    }

    [Fact]
    public async Task Corrupt_manifest_is_set_aside_and_rebuilt()
    {
        await NewRepository().SaveUserClipAsync("hour.12", PackBuilder.ClipFor("hour.12"));
        await File.WriteAllTextAsync(_locations.ManifestPath, "{ this is not json");

        var repo = NewRepository();
        await repo.InitializeAsync();

        Assert.True(repo.IsAvailable("hour.12"));
        Assert.Single(Directory.GetFiles(_locations.VoiceDirectory, "manifest.json.corrupt-*"));
        Assert.NotNull(JsonNode.Parse(await File.ReadAllTextAsync(_locations.ManifestPath))!["clips"]!["hour.12"]);
    }

    [Fact]
    public async Task Files_deleted_outside_the_app_become_missing_and_leave_the_manifest()
    {
        await NewRepository().SaveUserClipAsync("minute.01", PackBuilder.ClipFor("minute.01"));
        File.Delete(Path.Combine(_locations.UserClipsDirectory, "minute.01.wav"));

        var repo = NewRepository();
        await repo.InitializeAsync();

        Assert.Equal(AssetState.Missing, repo.GetStatus("minute.01").State);
        Assert.Null(JsonNode.Parse(await File.ReadAllTextAsync(_locations.ManifestPath))!["clips"]!["minute.01"]);
    }

    [Fact]
    public async Task Unrelated_files_in_the_folder_are_ignored_and_kept()
    {
        Directory.CreateDirectory(_locations.UserClipsDirectory);
        var stray = Path.Combine(_locations.UserClipsDirectory, "hour.13.wav");
        await File.WriteAllBytesAsync(stray, WavCodec.Encode(PackBuilder.ClipFor("x")));

        var repo = NewRepository();
        await repo.InitializeAsync();

        Assert.True(File.Exists(stray));
        Assert.Equal(AssetState.Missing, repo.GetStatus("hour.01").State);
    }

    [Fact]
    public async Task Concurrent_saves_of_different_clips_all_persist()
    {
        var repo = NewRepository();
        var ids = Enumerable.Range(1, 20).Select(m => $"minute.{m:00}").ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(() => repo.SaveUserClipAsync(id, PackBuilder.ClipFor(id)))));

        var reopened = NewRepository();
        await reopened.InitializeAsync();
        Assert.All(ids, id => Assert.True(reopened.IsAvailable(id), id));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(_locations.ManifestPath))!["clips"]!.AsObject();
        Assert.Equal(20, manifest.Count);
    }

    [Fact]
    public async Task Replacing_the_same_clip_concurrently_leaves_one_consistent_valid_version()
    {
        var repo = NewRepository();
        var versions = Enumerable.Range(0, 8).Select(i => PackBuilder.ClipFor("v" + i)).ToList();

        await Task.WhenAll(versions.Select(v => Task.Run(() => repo.SaveUserClipAsync("minute.30", v))));

        var loaded = (await NewRepository().LoadAsync("minute.30"))!;
        Assert.Contains(versions, v => v.Samples.AsSpan().SequenceEqual(loaded.Samples));
        Assert.Empty(Directory.GetFiles(_locations.VoiceDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Full_phrase_overrides_are_listed_when_recorded()
    {
        var repo = NewRepository();
        await repo.SaveUserClipAsync("phrase.01.43", PackBuilder.ClipFor("phrase.01.43"));

        Assert.Equal(new[] { "phrase.01.43" }, repo.AvailableFullPhraseIds.ToArray());
    }

    [Fact]
    public async Task Backup_of_own_recordings_restores_into_a_fresh_install()
    {
        var repo = NewRepository(new FakeBundledSource(PackBuilder.Build(["hour.01"])));
        var mine = PackBuilder.ClipFor("my minute");
        await repo.SaveUserClipAsync("minute.43", mine);
        await repo.SaveUserClipAsync("phrase.01.43", PackBuilder.ClipFor("phrase"));

        using var backup = new MemoryStream();
        var exported = await repo.ExportAsync(backup, VoicePackScope.UserRecordings, "1.0");
        Assert.Equal(2, exported);

        using var other = new TempDirectory();
        var fresh = new VoiceAssetRepository(new StorageLocations(other.Path), TestCatalog.Catalog, new NoBundledVoicePack());
        backup.Position = 0;
        var result = await fresh.ImportAsync(backup);

        Assert.True(result.Accepted);
        Assert.Equal(2, result.Imported);
        Assert.Empty(result.Problems);
        Assert.Equal(AssetSource.User, fresh.GetStatus("minute.43").Source);
        Assert.True(fresh.IsAvailable("phrase.01.43"));
        Assert.False(fresh.IsAvailable("hour.01"));   // defaults are not part of a personal backup
        Assert.Equal(mine.SampleCount, (await fresh.LoadAsync("minute.43"))!.SampleCount, tolerance: 2 * 441);
    }

    [Fact]
    public async Task Full_export_includes_defaults_and_matching_defaults_are_not_duplicated_on_import()
    {
        var source = new FakeBundledSource(PackBuilder.Build(["hour.01", "hour.02"]));
        var repo = NewRepository(source);
        await repo.SaveUserClipAsync("hour.03", PackBuilder.ClipFor("hour.03"));

        using var pack = new MemoryStream();
        Assert.Equal(3, await repo.ExportAsync(pack, VoicePackScope.Everything));

        // Re-import into an install that ships the same defaults.
        using var other = new TempDirectory();
        var sameApp = new VoiceAssetRepository(new StorageLocations(other.Path), TestCatalog.Catalog, new FakeBundledSource(source.Pack));
        pack.Position = 0;
        var result = await sameApp.ImportAsync(pack);

        Assert.Equal(1, result.Imported);
        Assert.Equal(2, result.MatchedDefault);
        Assert.Equal(AssetSource.Bundled, sameApp.GetStatus("hour.01").Source);
        Assert.Equal(AssetSource.User, sameApp.GetStatus("hour.03").Source);
    }

    [Fact]
    public async Task Import_rejects_files_that_are_not_voice_packs()
    {
        var repo = NewRepository();
        var result = await repo.ImportAsync(new MemoryStream([1, 2, 3]));
        Assert.False(result.Accepted);
        Assert.Equal(VoicePackProblemKind.NotAZip, Assert.Single(result.Problems).Kind);
    }

    [Theory]
    [InlineData("wrong-format", VoicePackProblemKind.WrongFormat)]
    [InlineData("future-version", VoicePackProblemKind.UnsupportedVersion)]
    [InlineData("no-manifest", VoicePackProblemKind.MissingManifest)]
    [InlineData("bad-json", VoicePackProblemKind.InvalidManifest)]
    public async Task Import_rejects_invalid_pack_metadata(string mutation, VoicePackProblemKind expected)
    {
        var pack = PackBuilder.Build(["hour.01"]);
        pack = mutation switch
        {
            "wrong-format" => PackBuilder.Mutate(pack, manifest: m => m["format"] = "something-else"),
            "future-version" => PackBuilder.Mutate(pack, manifest: m => m["schemaVersion"] = 99),
            "no-manifest" => PackBuilder.Mutate(pack, archive: z => z.GetEntry(VoicePackFormat.ManifestEntryName)!.Delete()),
            _ => PackBuilder.Mutate(pack, archive: z => PackBuilder.ReplaceEntry(z, VoicePackFormat.ManifestEntryName, "{ nope"u8.ToArray())),
        };

        var repo = NewRepository();
        var result = await repo.ImportAsync(new MemoryStream(pack));

        Assert.False(result.Accepted);
        Assert.Contains(result.Problems, p => p.Kind == expected);
        Assert.Equal(AssetState.Missing, repo.GetStatus("hour.01").State);
    }

    [Fact]
    public async Task Import_skips_bad_clips_but_keeps_good_ones()
    {
        var good = PackBuilder.Build(["hour.01", "hour.02", "hour.03", "hour.04", "hour.05", "hour.06"]);
        var pack = PackBuilder.Mutate(
            good,
            manifest: m =>
            {
                var clips = m["clips"]!.AsArray();
                clips[1]!["file"] = "../../hour.02.wav";                  // path traversal attempt
                clips[2]!["id"] = "hour.13";                              // unknown id
                clips[3]!["sha256"] = new string('0', 64);                // checksum mismatch
                clips[4]!.AsObject().Remove("sha256");                    // silent audio must fail validation itself
                clips.Add(JsonNode.Parse(clips[0]!.ToJsonString()));    // duplicate of hour.01
            },
            archive: z => PackBuilder.ReplaceEntry(z, VoicePackFormat.ClipEntryName("hour.05"), WavCodec.Encode(Signals.Silence(800))));

        var repo = NewRepository();
        var result = await repo.ImportAsync(new MemoryStream(pack));

        Assert.True(result.Accepted);
        Assert.Equal(2, result.Imported);   // hour.01 and hour.06
        Assert.True(repo.IsAvailable("hour.01"));
        Assert.True(repo.IsAvailable("hour.06"));
        Assert.False(repo.IsAvailable("hour.02"));
        Assert.False(repo.IsAvailable("hour.04"));
        Assert.False(repo.IsAvailable("hour.05"));
        Assert.Contains(result.Problems, p => p.Kind == VoicePackProblemKind.UnexpectedPath);
        Assert.Contains(result.Problems, p => p.Kind == VoicePackProblemKind.UnknownClip);
        Assert.Contains(result.Problems, p => p.Kind == VoicePackProblemKind.ChecksumMismatch);
        Assert.Contains(result.Problems, p => p.Kind == VoicePackProblemKind.DuplicateClip);
        Assert.Contains(result.Problems, p => p.Kind == VoicePackProblemKind.InvalidAudio && p.ClipId == "hour.05");
    }

    [Fact]
    public async Task Import_refuses_to_inflate_oversized_entries()
    {
        var huge = new byte[(int)VoicePackFormat.MaxClipBytes + 1024]; // compresses to almost nothing
        var pack = PackBuilder.Mutate(
            PackBuilder.Build(["hour.01"]),
            manifest: m => m["clips"]![0]!.AsObject().Remove("sha256"),
            archive: z => PackBuilder.ReplaceEntry(z, VoicePackFormat.ClipEntryName("hour.01"), huge));

        var repo = NewRepository();
        var result = await repo.ImportAsync(new MemoryStream(pack));

        Assert.Equal(0, result.Imported);
        Assert.Contains(result.Problems, p => p.Kind == VoicePackProblemKind.InvalidAudio);
    }
}

internal static class RepositoryTestExtensions
{
    public static AssetState GetStatusAfterInit(this VoiceAssetRepository repository, string id)
    {
        repository.InitializeAsync().GetAwaiter().GetResult();
        return repository.GetStatus(id).State;
    }
}
