// Polyfills for .NET Framework 4.8 — types/methods present on net6+ but missing on net48.
// PolySharp handles type-level polyfills (Index, Range, IsExternalInit); this file handles
// the rest (instance methods that can't be polyfilled by PolySharp).
//
// The namespace deliberately lives outside this file's folder so the extension method is
// discoverable wherever `System.Collections.Generic` is in scope (which is everywhere via
// Directory.Build.props's <Using> items) — without forcing every consumer to add a
// `using LambdaBoss.Common;`.

// ReSharper disable once CheckNamespace
namespace System.Collections.Generic;

internal static class KeyValuePairPolyfillExtensions
{
    /// <summary>
    ///     Polyfill for KeyValuePair&lt;TKey, TValue&gt;.Deconstruct on net48 so
    ///     <c>foreach (var (k, v) in dict)</c> compiles.
    /// </summary>
    public static void Deconstruct<TKey, TValue>(
        this KeyValuePair<TKey, TValue> kvp,
        out TKey key,
        out TValue value)
    {
        key = kvp.Key;
        value = kvp.Value;
    }
}
