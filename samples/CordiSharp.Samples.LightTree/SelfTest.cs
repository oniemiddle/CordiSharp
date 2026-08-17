namespace CordiSharp.Samples.LightTree;

/// <summary>
/// Headless cascade verification: drives the exact CordiSharp operations the app uses
/// (ctx.Inject + ctx.Provide, strict resolution) and prints each fiber's state after
/// every step, so a broken cascade is immediately visible in the console.
/// Run with: dotnet run --project samples/CordiSharp.Samples.LightTree -- --selftest
/// </summary>
internal static class SelfTest
{
    public static async Task Run()
    {
        Console.WriteLine("== CordiSharp cascade self-test ==");
        var root = Context.Create();

        // N2 depends on svc:1, which is NOT provided yet → expect Pending
        var n2 = root.Inject(["svc:1"], (ctx, _) => { ctx.Provide("svc:2", "m2"); return null; });
        Console.WriteLine($"N2 after load        : {n2.State}   (expect Pending)");

        // N1 provides svc:1 → N2 should cascade to Active on its own
        var n1 = root.Inject([], (ctx, _) => { ctx.Provide("svc:1", "m1"); return null; });
        Console.WriteLine($"N1 after load        : {n1.State}   (expect Loading or Active)");
        await n1.Await();
        Console.WriteLine($"N1 settled           : {n1.State}   (expect Active)");
        await Task.Delay(100);
        Console.WriteLine($"N2 after N1 active   : {n2.State}   (expect Active — cascade up)");

        // N3 depends on N2's svc:2 (already provided) → expect Active directly
        await n2.Await();
        var n3 = root.Inject(["svc:2"], (ctx, _) => { ctx.Provide("svc:3", "m3"); return null; });
        await n3.Await();
        await Task.Delay(100);
        Console.WriteLine($"N3 after load        : {n3.State}   (expect Active)");

        // dispose N1 → N2, N3 should cascade to Pending
        await n1.DisposeAsync();
        await Task.Delay(100);
        Console.WriteLine($"N2 after N1 disposed : {n2.State}   (expect Pending — cascade down)");
        Console.WriteLine($"N3 after N1 disposed : {n3.State}   (expect Pending — cascade down)");

        // restore N1 → cascade back up
        var n1b = root.Inject([], (ctx, _) => { ctx.Provide("svc:1", "m1b"); return null; });
        await n1b.Await();
        await Task.Delay(100);
        Console.WriteLine($"N2 after N1 restored : {n2.State}   (expect Active — cascade up again)");
        Console.WriteLine($"N3 after N1 restored : {n3.State}   (expect Active)");

        Console.WriteLine("== done ==");
    }
}
