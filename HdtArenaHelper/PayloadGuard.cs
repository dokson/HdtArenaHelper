using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HdtArenaHelper
{
	/// <summary>
	/// Treats every downloaded payload as UNTRUSTED input: the feeds are third-party endpoints that can
	/// serve whatever they like to whichever caller they like.
	///
	/// **Invariant: no RCE path.** Parsing is <c>JObject.Parse</c>/<c>Load</c> only — never
	/// <c>DeserializeObject&lt;T&gt;</c>, never <c>TypeNameHandling</c>, which is what turns a JSON
	/// document into a gadget chain. Do not add either to a remote-data parse path.
	///
	/// Bounded here, each one a real payload away: deep nesting (HDT ships Newtonsoft 12.0.3, whose
	/// <c>MaxDepth</c> default is unlimited, and a StackOverflowException cannot be caught — it takes
	/// the tracker down, not just us), a gzip bomb from the .gz.json files, an oversized body, and
	/// out-of-range numbers — DROPPED rather than clamped, so a poisoned row falls back to the other
	/// source instead of asserting a value the feed never reported.
	///
	/// Not addressable here: SUBTLE poisoning, e.g. shifting a class by 2pp, indistinguishable from a
	/// meta shift. The mitigation is the two-source consensus.
	/// </summary>
	internal static class PayloadGuard
	{
		/// <summary>
		/// Ceiling for a raw or decompressed payload. The real ones are ~100 KB compressed and a few
		/// MB raw, so this is generous by an order of magnitude while still bounded.
		/// </summary>
		internal const int MaxPayloadBytes = 24 * 1024 * 1024;

		/// <summary>
		/// Nesting depth allowed while parsing. The real documents are 4 levels deep; 32 leaves room
		/// for a format change and still refuses a document built to blow the stack.
		/// </summary>
		internal const int MaxJsonDepth = 32;

		/// <summary>
		/// Parse a JSON object with a bounded depth, or null if it is malformed, too deep, or too
		/// large. Callers already treat null as "no usable data" and fall back to cache/other sources.
		/// </summary>
		internal static JObject? ParseObject(string? json)
		{
			if(string.IsNullOrEmpty(json) || json!.Length > MaxPayloadBytes)
				return null;
			try
			{
				using(var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = MaxJsonDepth })
				{
					var obj = JObject.Load(reader);
					// Anything after the top-level object is not a document we understand.
					return reader.Read() ? null : obj;
				}
			}
			catch(JsonException)
			{
				return null;
			}
		}

		/// <summary>
		/// Gunzip with a hard ceiling on the OUTPUT, so a small file cannot expand into an
		/// out-of-memory. Returns null when the limit is hit — deliberately not "as much as fits",
		/// because a truncated JSON document is not a smaller truth, it is a parse error waiting to
		/// look like a data problem.
		/// </summary>
		internal static string? Gunzip(byte[]? compressed)
		{
			if(compressed == null || compressed.Length == 0 || compressed.Length > MaxPayloadBytes)
				return null;
			try
			{
				using(var gz = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress))
				using(var bounded = new MemoryStream())
				{
					var buffer = new byte[64 * 1024];
					var total = 0;
					int read;
					while((read = gz.Read(buffer, 0, buffer.Length)) > 0)
					{
						total += read;
						if(total > MaxPayloadBytes)
							return null;
						bounded.Write(buffer, 0, read);
					}
					return Encoding.UTF8.GetString(bounded.ToArray());
				}
			}
			catch(Exception ex) when(ex is InvalidDataException || ex is IOException)
			{
				return null;
			}
		}

		/// <summary>A percentage the feed reported, or null when it is outside [0, 100].</summary>
		internal static double? Percent(double? value)
			=> value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
				|| value.Value < 0 || value.Value > 100
					? (double?)null
					: value;

		/// <summary>A non-negative count the feed reported, or null when it is negative.</summary>
		internal static double? Count(double? value)
			=> value == null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
				|| value.Value < 0
					? (double?)null
					: value;
	}
}
