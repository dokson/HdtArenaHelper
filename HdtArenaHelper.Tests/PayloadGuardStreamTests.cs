using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HdtArenaHelper.Tests
{
	/// <summary>
	/// Covers the STREAMING parse, which the leaderboard crawl uses so a ~291 KB response never becomes a
	/// byte array and a ~582 KB string on the way to a JObject. The point of these tests is that reading
	/// this way keeps the guarantees the buffered overload had: bounded size, bounded depth, and nothing
	/// but <c>JObject.Load</c> in the parse path. A byte ceiling that only holds for the string overload
	/// would be no ceiling at all, since the crawl no longer goes through it.
	/// </summary>
	public class PayloadGuardStreamTests
	{
		private static Stream Utf8(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

		[Fact]
		public async Task Parses_a_well_formed_object()
		{
			var obj = await PayloadGuard.ParseObjectAsync(
				Utf8("{\"seasonId\":1,\"leaderboard\":{\"rows\":[{\"rank\":1}]}}"), CancellationToken.None);

			Assert.NotNull(obj);
			Assert.Equal(1, (int)obj!["seasonId"]!);
		}

		[Fact]
		public async Task Rejects_malformed_json()
		{
			Assert.Null(await PayloadGuard.ParseObjectAsync(Utf8("not json"), CancellationToken.None));
		}

		[Fact]
		public async Task Rejects_a_null_stream()
		{
			Assert.Null(await PayloadGuard.ParseObjectAsync(null, CancellationToken.None));
		}

		[Fact]
		public async Task Rejects_content_after_the_top_level_object()
		{
			// Two documents back to back is not a document we understand, and accepting the first would
			// silently ignore whatever followed it.
			Assert.Null(await PayloadGuard.ParseObjectAsync(
				Utf8("{\"seasonId\":1} {\"seasonId\":2}"), CancellationToken.None));
		}

		[Fact]
		public async Task Rejects_a_document_nested_past_the_depth_ceiling()
		{
			// HDT ships Newtonsoft 12.0.3, whose MaxDepth default is unlimited, and a StackOverflowException
			// cannot be caught — it would take the whole tracker down rather than just this feature.
			var deep = new StringBuilder();
			for(var i = 0; i < PayloadGuard.MaxJsonDepth + 10; i++)
				deep.Append("{\"a\":");
			deep.Append('1');
			for(var i = 0; i < PayloadGuard.MaxJsonDepth + 10; i++)
				deep.Append('}');

			Assert.Null(await PayloadGuard.ParseObjectAsync(Utf8(deep.ToString()), CancellationToken.None));
		}

		[Fact]
		public async Task Rejects_a_body_past_the_byte_ceiling_WHILE_reading_it()
		{
			// The ceiling has to bite during the read: a caller that has already buffered the whole body
			// has nothing left to protect. Padding inside a string keeps the document well-formed, so a
			// null result can only come from the size guard rather than from a parse failure.
			var oversized = new StringBuilder("{\"pad\":\"");
			oversized.Append('x', PayloadGuard.MaxPayloadBytes + 1024);
			oversized.Append("\"}");

			Assert.Null(await PayloadGuard.ParseObjectAsync(Utf8(oversized.ToString()), CancellationToken.None));
		}

		[Fact]
		public async Task Accepts_a_body_comfortably_larger_than_a_real_page()
		{
			// A real page is ~291 KB decompressed, so the ceiling must not be anywhere near it: this is the
			// guard against "fixed" the size limit by making it too tight to work.
			var rows = new StringBuilder("{\"seasonId\":1,\"leaderboard\":{\"rows\":[");
			for(var i = 1; i <= 20000; i++)
			{
				if(i > 1)
					rows.Append(',');
				rows.Append($"{{\"rank\":{i},\"accountid\":\"Player{i}\",\"rating\":5.5}}");
			}
			rows.Append("]}}");
			Assert.True(rows.Length > 291 * 1024, "fixture should exceed a real page's decompressed size");

			var obj = await PayloadGuard.ParseObjectAsync(Utf8(rows.ToString()), CancellationToken.None);

			Assert.NotNull(obj);
		}
	}
}
