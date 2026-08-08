using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HearthDb;
using HearthDb.Enums;

namespace HdtArenaHelper.Training
{
	/// <summary>
	/// Dumps every COLLECTIBLE card HearthDb knows about, grouped by <see cref="CardSet"/>,
	/// into two repo-committed reference files: <c>docs/HSDatabase.md</c> to grep and
	/// <c>Generated/HSDatabase.g.cs</c> for the test projects to compile. Named for what they
	/// hold — three databases, cards and hero powers and heroes — rather than for cards alone.
	/// Not part of the ridge fit and NOT part of the plugin build — the plugin reads HearthDb
	/// directly, so nothing here can change a score or the shipped DLL's size.
	/// Re-run by CI (<c>card-database.yml</c>) so it never drifts from the pinned HDT version.
	/// </summary>
	internal static class HSDatabaseGenerator
	{
		// Every output line is written with an explicit LF: the repo is LF-only (.gitattributes
		// normalizes and the pre-commit hook blocks CRLF), and AppendLine would emit
		// Environment.NewLine — CRLF here, LF on a Linux runner, for the same input.
		private const string Nl = "\n";

		// Entries per generated Fill method. One array initializer holding all ~7,400 cards is a
		// single IL method body, and the runtime caps that at 64 KB — this file was never compiled
		// before, so the cap was never hit. 400 keeps each method an order of magnitude clear.
		private const int ChunkSize = 400;

		// The GameTags the plugin's rules actually read (MetadataSynergyEngine's categories and
		// dead-card lever, DeckMulliganAdvisor's Combo/Tradeable/quest rules), plus the keywords
		// the heuristic reads off the card. Names match the CardFlags enum emitted below.
		private static readonly (string Name, GameTag Tag)[] FlagTags =
		{
			("Elite", GameTag.ELITE),
			("Taunt", GameTag.TAUNT),
			("DivineShield", GameTag.DIVINE_SHIELD),
			("Windfury", GameTag.WINDFURY),
			("Poisonous", GameTag.POISONOUS),
			("Reborn", GameTag.REBORN),
			("Deathrattle", GameTag.DEATHRATTLE),
			("Battlecry", GameTag.BATTLECRY),
			("Combo", GameTag.COMBO),
			("Secret", GameTag.SECRET),
			("Aura", GameTag.PALADIN_AURA),
			("Quest", GameTag.QUEST),
			("Questline", GameTag.QUESTLINE),
			("Sidequest", GameTag.SIDEQUEST),
			("Tradeable", GameTag.TRADEABLE),
		};

		internal static void Run(string repoRoot)
		{
			var (files, totalCards, sets, version) = Build();

			Directory.CreateDirectory(Path.Combine(repoRoot, "Generated"));
			foreach(var file in files)
			{
				var path = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
				File.WriteAllText(path, file.Content);
				Console.WriteLine($"wrote {path} ({file.Entries} entries)");
			}

			Console.WriteLine($"{totalCards} cards, {sets} sets, HearthDb {version}");
		}

		/// <summary>One generated file: its repo-relative path and exactly what should be in it.</summary>
		internal sealed class GeneratedFile
		{
			public string Path { get; }
			public string Content { get; }
			public int Entries { get; }

			public GeneratedFile(string path, string content, int entries)
			{
				Path = path;
				Content = content;
				Entries = entries;
			}
		}

		/// <summary>
		/// The exact contents every committed file should have for the current HearthDb. Exposed so
		/// the drift test can compare against the files byte-for-byte using THIS code rather than a
		/// second copy of the projection — a re-implementation is the mistake that let a regression
		/// test pass either way in 0.1.6.
		///
		/// A LIST of files rather than a fixed pair: the pool is three databases now, and the drift
		/// test iterates whatever this returns, so a fourth costs nothing beyond generating it.
		/// </summary>
		internal static (IReadOnlyList<GeneratedFile> Files, int TotalCards, int Sets, string Version) Build()
		{
			// Everything a rule here can be asked about: collectible cards, plus every HERO_POWER and
			// every HERO. Neither of the last two is COLLECTIBLE — measured, which also means the
			// `Type != HERO_POWER` filter this replaced never excluded anything — so both have to be
			// pulled from All by type. They are in because HeroPowerThreat classifies hero powers and
			// ScoreMath maps hero skins, and their fixtures were the last place holding raw card ids.
			//
			// Deliberately NOT all of Cards.All: the ~23k cards outside this union are tokens,
			// enchantments and adventure cards that no rule is ever handed, and measured on the pool
			// they carry thousands of duplicate names (one recurs 79 times, named "???"). Pulling them
			// in would drown the named accessors in set suffixes, which is the one thing this file
			// exists to provide.
			var pool = Cards.All.Values
				.Where(c => c.Collectible || c.Type == CardType.HERO_POWER || c.Type == CardType.HERO);

			// Materialized once: the groups feed both writers, and a deferred IEnumerable would
			// re-group and re-sort the whole pool for the second one.
			var sets = pool
				.GroupBy(c => c.Set)
				.OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
				.Select(g => (Set: g.Key, Cards: (IReadOnlyList<Card>)g
					.OrderBy(c => c.Cost)
					.ThenBy(c => c.Name, StringComparer.Ordinal)
					// Total order: without this, two cards sharing a set, cost and name fall back
					// to dictionary enumeration order and the output stops being reproducible.
					.ThenBy(c => c.Id, StringComparer.Ordinal)
					.ToList()))
				.ToList();

			var totalCards = sets.Sum(s => s.Cards.Count);
			var version = typeof(Cards).Assembly.GetName().Version?.ToString() ?? "unknown";

			// One markdown file per KIND, mirroring the three C# databases. Reading is the whole
			// purpose of the markdown, and a reader looking up a hero power should not have to scroll
			// past 8,000 collectible cards to reach it.
			var files = new List<GeneratedFile>
			{
				// kebab-case to match the rest of docs/ (hearthstone-primer.md, hdt-logo.svg).
				Markdown(sets, version, "docs/hearthstone-cards.md", "Card database",
					c => c.Type != CardType.HERO_POWER && c.Type != CardType.HERO),
				Markdown(sets, version, "docs/hearthstone-hero-powers.md", "Hero power database",
					c => c.Type == CardType.HERO_POWER),
				Markdown(sets, version, "docs/hearthstone-heroes.md", "Hero database",
					c => c.Type == CardType.HERO),
				new GeneratedFile("Generated/HSDatabase.g.cs",
					BuildCsSource(sets, totalCards, version), totalCards),
			};

			return (files, totalCards, sets.Count, version);
		}

		private static GeneratedFile Markdown(
			IReadOnlyList<(CardSet Set, IReadOnlyList<Card> Cards)> allSets, string version,
			string path, string title, Func<Card, bool> keep)
		{
			var sets = allSets
				.Select(s => (s.Set, Cards: (IReadOnlyList<Card>)s.Cards.Where(keep).ToList()))
				.Where(s => s.Cards.Count > 0)
				.ToList();
			var totalCards = sets.Sum(s => s.Cards.Count);

			return new GeneratedFile(path, BuildMarkdown(sets, totalCards, version, title), totalCards);
		}

		private static string BuildMarkdown(
			IReadOnlyList<(CardSet Set, IReadOnlyList<Card> Cards)> sets, int totalCards, string version,
			string title)
		{
			var sb = new StringBuilder();
			sb.Append($"<!-- {totalCards} cards, {sets.Count} sets, HearthDb {version} -->").Append(Nl);
			sb.Append($"# {title}").Append(Nl).Append(Nl);
			sb.Append($"Auto-generated by `HdtArenaHelper.Training -- --dump-database` from **HearthDb {version}**")
				.Append(Nl);
			sb.Append("(the card DB bundled with HDT, pinned by `hdt-version.txt` — not by")
				.Append(Nl);
			sb.Append("`global.json`, which pins the SDK), grouped by `CardSet` — do not hand-edit, it is")
				.Append(Nl);
			sb.Append("overwritten by `.github/workflows/card-database.yml`.")
				.Append(Nl).Append(Nl);
			sb.Append("Cards, hero powers and heroes are three separate files here and three separate")
				.Append(Nl);
			sb.Append("databases in `Generated/HSDatabase.g.cs`, which the test projects compile; a drift")
				.Append(Nl);
			sb.Append("test fails if any of them stops matching HearthDb.")
				.Append(Nl).Append(Nl);

			foreach(var (set, cards) in sets)
			{
				sb.Append($"## {set} ({cards.Count})").Append(Nl).Append(Nl);
				sb.Append("| Name | Cost | Atk | Health | Dur | Class | Type | Race | Race2 | School | Rarity | Flags | CardId | DbfId | Text |")
					.Append(Nl);
				sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|").Append(Nl);
				foreach(var card in cards)
				{
					sb.Append($"| {Cell(card.Name)} | {card.Cost} | {card.Attack} | {card.Health} | ")
						.Append($"{card.Durability} | {card.Class} | {card.Type} | {card.Race} | ")
						.Append($"{card.SecondaryRace} | {SpellSchoolName(card)} | {card.Rarity} | ")
						.Append($"{FlagList(card)} | {card.Id} | {card.DbfId} | {Cell(CardText.Flattened(card))} |")
						.Append(Nl);
				}
				sb.Append(Nl);
			}

			return sb.ToString();
		}

		// Plain data, no HearthDb/HDT reference: any project (even one with no HSDTPath at all)
		// can add this single file with <Compile Include="..\Generated\HSDatabase.g.cs" />
		// and get the whole pool as C# — no JSON parsing, no runtime dependency. Deliberately
		// not referenced by the plugin csproj: it would bloat the shipped DLL for data the
		// plugin already has from HearthDb at runtime.
		private static string BuildCsSource(
			IReadOnlyList<(CardSet Set, IReadOnlyList<Card> Cards)> sets, int totalCards, string version)
		{
			var sb = new StringBuilder();
			sb.Append("// <auto-generated>").Append(Nl);
			sb.Append("// Generated by `dotnet run --project HdtArenaHelper.Training -- --dump-database`.").Append(Nl);
			sb.Append($"// Source: HearthDb {version} — {totalCards} collectible cards, {sets.Count} sets.").Append(Nl);
			sb.Append("// Do not hand-edit — overwritten by .github/workflows/card-database.yml.").Append(Nl);
			sb.Append("// </auto-generated>").Append(Nl).Append(Nl);
			sb.Append("using System;").Append(Nl);
			sb.Append("using System.Collections.Generic;").Append(Nl).Append(Nl);
			sb.Append("namespace HdtArenaHelper.CardDatabase").Append(Nl);
			sb.Append("{").Append(Nl);

			WriteFlagsEnum(sb);
			WriteEntryStruct(sb);
			WriteEntries(sb, sets, totalCards);

			sb.Append("}").Append(Nl);

			return sb.ToString();
		}

		private static void WriteFlagsEnum(StringBuilder sb)
		{
			sb.Append("\t/// <summary>Card properties the plugin's rules read off a GameTag.</summary>").Append(Nl);
			sb.Append("\t[Flags]").Append(Nl);
			sb.Append("\tpublic enum CardFlags").Append(Nl);
			sb.Append("\t{").Append(Nl);
			sb.Append("\t\tNone = 0,").Append(Nl);
			for(var i = 0; i < FlagTags.Length; i++)
				sb.Append($"\t\t{FlagTags[i].Name} = 1 << {i},").Append(Nl);
			sb.Append("\t}").Append(Nl).Append(Nl);
		}

		private static void WriteEntryStruct(StringBuilder sb)
		{
			sb.Append("\t/// <summary>One collectible card's metadata, as HearthDb reported it.</summary>").Append(Nl);
			sb.Append("\tpublic readonly struct CardEntry").Append(Nl);
			sb.Append("\t{").Append(Nl);
			sb.Append("\t\tpublic string CardId { get; }").Append(Nl);
			sb.Append("\t\tpublic int DbfId { get; }").Append(Nl);
			sb.Append("\t\tpublic string Name { get; }").Append(Nl);
			sb.Append("\t\tpublic int Cost { get; }").Append(Nl);
			sb.Append("\t\tpublic int Attack { get; }").Append(Nl);
			sb.Append("\t\tpublic int Health { get; }").Append(Nl);
			sb.Append("\t\tpublic int Durability { get; }").Append(Nl);
			sb.Append("\t\tpublic string Class { get; }").Append(Nl);
			sb.Append("\t\tpublic string Type { get; }").Append(Nl);
			sb.Append("\t\tpublic string Race { get; }").Append(Nl);
			sb.Append("\t\tpublic string SecondaryRace { get; }").Append(Nl);
			sb.Append("\t\tpublic string SpellSchool { get; }").Append(Nl);
			sb.Append("\t\tpublic string Rarity { get; }").Append(Nl);
			sb.Append("\t\tpublic string Set { get; }").Append(Nl);
			sb.Append("\t\tpublic string Text { get; }").Append(Nl);
			sb.Append("\t\tpublic CardFlags Flags { get; }").Append(Nl).Append(Nl);
			sb.Append("\t\tpublic CardEntry(string cardId, int dbfId, string name, int cost, int attack, int health,")
				.Append(Nl);
			sb.Append("\t\t\tint durability, string cardClass, string type, string race, string secondaryRace,")
				.Append(Nl);
			sb.Append("\t\t\tstring spellSchool, string rarity, string set, string text, CardFlags flags)").Append(Nl);
			sb.Append("\t\t{").Append(Nl);
			sb.Append("\t\t\tCardId = cardId; DbfId = dbfId; Name = name; Cost = cost; Attack = attack;").Append(Nl);
			sb.Append("\t\t\tHealth = health; Durability = durability; Class = cardClass; Type = type;").Append(Nl);
			sb.Append("\t\t\tRace = race; SecondaryRace = secondaryRace; SpellSchool = spellSchool;").Append(Nl);
			sb.Append("\t\t\tRarity = rarity; Set = set; Text = text; Flags = flags;").Append(Nl);
			sb.Append("\t\t}").Append(Nl).Append(Nl);
			sb.Append("\t\tpublic bool Has(CardFlags flag) => (Flags & flag) != 0;").Append(Nl);
			sb.Append("\t}").Append(Nl).Append(Nl);
		}

		private static void WriteEntries(StringBuilder sb,
			IReadOnlyList<(CardSet Set, IReadOnlyList<Card> Cards)> sets, int totalCards)
		{
			var all = sets.SelectMany(s => s.Cards.Select(c => (Card: c, Set: s.Set))).ToList();

			// THREE databases, not one list with three kinds of thing in it. A card, a hero power and
			// a hero are not interchangeable: nothing that takes a card should ever be handed a hero
			// power, and "Icy Touch" being both a Death Knight spell and a hero power made that a real
			// mix-up rather than a theoretical one. Splitting the LISTS as well as the named accessors
			// means a caller cannot iterate the pool and meet something it has no rule for.
			WriteDatabase(sb, "CardDatabase", "Every playable collectible card",
				all.Where(c => c.Card.Type != CardType.HERO_POWER && c.Card.Type != CardType.HERO).ToList());
			WriteDatabase(sb, "HeroPowerDatabase", "Every hero power",
				all.Where(c => c.Card.Type == CardType.HERO_POWER).ToList());
			WriteDatabase(sb, "HeroDatabase", "Every hero",
				all.Where(c => c.Card.Type == CardType.HERO).ToList());

			// Three classes, not one, for the same reason and with the same split.
			WriteNamedAccessors(sb, "HSCard", "CardDatabase", "Every playable card in the pool, by name.",
				all.Where(c => c.Card.Type != CardType.HERO_POWER && c.Card.Type != CardType.HERO).ToList());
			WriteNamedAccessors(sb, "HSHeroPower", "HeroPowerDatabase", "Every hero power, by name.",
				all.Where(c => c.Card.Type == CardType.HERO_POWER).ToList());
			WriteNamedAccessors(sb, "HSHero", "HeroDatabase", "Every hero, by name.",
				all.Where(c => c.Card.Type == CardType.HERO).ToList());
		}

		private static void WriteDatabase(StringBuilder sb, string className, string summary,
			IReadOnlyList<(Card Card, CardSet Set)> all)
		{
			var chunks = (all.Count + ChunkSize - 1) / ChunkSize;
			var totalCards = all.Count;

			sb.Append($"\tpublic static class {className}").Append(Nl);
			sb.Append("\t{").Append(Nl);
			sb.Append($"\t\t/// <summary>{summary}, ordered by set, cost, name, id.</summary>")
				.Append(Nl);
			sb.Append("\t\tpublic static readonly IReadOnlyList<CardEntry> All = Build();").Append(Nl).Append(Nl);
			sb.Append("\t\tprivate static IReadOnlyList<CardEntry> Build()").Append(Nl);
			sb.Append("\t\t{").Append(Nl);
			sb.Append($"\t\t\tvar cards = new List<CardEntry>({totalCards});").Append(Nl);
			for(var i = 0; i < chunks; i++)
				sb.Append($"\t\t\tFill{i.ToString(CultureInfo.InvariantCulture)}(cards);").Append(Nl);
			sb.Append("\t\t\treturn cards;").Append(Nl);
			sb.Append("\t\t}").Append(Nl);

			for(var i = 0; i < chunks; i++)
			{
				sb.Append(Nl);
				sb.Append($"\t\tprivate static void Fill{i.ToString(CultureInfo.InvariantCulture)}(List<CardEntry> cards)")
					.Append(Nl);
				sb.Append("\t\t{").Append(Nl);
				foreach(var (card, set) in all.Skip(i * ChunkSize).Take(ChunkSize))
				{
					sb.Append($"\t\t\tcards.Add(new CardEntry({Escape(card.Id)}, {card.DbfId}, {Escape(card.Name)}, ")
						.Append($"{card.Cost}, {card.Attack}, {card.Health}, {card.Durability}, ")
						.Append($"{Escape(card.Class.ToString())}, {Escape(card.Type.ToString())}, ")
						.Append($"{Escape(card.Race.ToString())}, {Escape(card.SecondaryRace.ToString())}, ")
						.Append($"{Escape(SpellSchoolName(card))}, {Escape(card.Rarity.ToString())}, ")
						.Append($"{Escape(set.ToString())}, {Escape(CardText.Flattened(card))}, {Flags(card)}));")
						.Append(Nl);
				}
				sb.Append("\t\t}").Append(Nl);
			}

			sb.Append("\t}").Append(Nl).Append(Nl);
		}

		/// <summary>
		/// One accessor per card, named after the card. This is the whole point of committing the
		/// pool: a fixture reading <c>HSCard.Tuskpiercer</c> says what it is testing, where
		/// <c>"BAR_330"</c> needs a trailing comment that nothing keeps true.
		/// Not <c>Cards</c>, which would collide with <c>HearthDb.Cards</c> in the test files that
		/// use both.
		/// </summary>
		private static void WriteNamedAccessors(StringBuilder sb, string className, string database,
			string summary, IReadOnlyList<(Card Card, CardSet Set)> all)
		{
			sb.Append(Nl);
			sb.Append($"\t/// <summary>{summary} See {database}.All for the list form.</summary>")
				.Append(Nl);
			sb.Append($"\tpublic static class {className}").Append(Nl);
			sb.Append("\t{").Append(Nl);
			sb.Append("\t\tprivate static readonly Dictionary<int, CardEntry> ById = BuildIndex();").Append(Nl);
			sb.Append(Nl);
			sb.Append("\t\t/// <summary>The card with this dbf id, for a fixture that has an id and no name.</summary>")
				.Append(Nl);
			sb.Append("\t\tpublic static CardEntry Get(int dbfId) => ById[dbfId];").Append(Nl);
			sb.Append(Nl);
			sb.Append("\t\tprivate static Dictionary<int, CardEntry> BuildIndex()").Append(Nl);
			sb.Append("\t\t{").Append(Nl);
			sb.Append($"\t\t\tvar index = new Dictionary<int, CardEntry>({database}.All.Count);").Append(Nl);
			sb.Append($"\t\t\tforeach(var card in {database}.All)").Append(Nl);
			sb.Append("\t\t\t\tindex[card.DbfId] = card;").Append(Nl);
			sb.Append("\t\t\treturn index;").Append(Nl);
			sb.Append("\t\t}").Append(Nl);

			foreach(var (card, identifier) in NameAccessors(all))
			{
				// The summary is what an IDE shows on hover, which is where a reader asks "why this
				// card?" — so it carries the statline and the text, not just the name again.
				sb.Append(Nl);
				sb.Append($"\t\t/// <summary>{Doc(card)}</summary>").Append(Nl);
				sb.Append($"\t\tpublic static CardEntry {identifier} => ById[{card.DbfId}];").Append(Nl);
			}

			sb.Append("\t}").Append(Nl);
		}

		/// <summary>
		/// The <c>HSCard.X</c> accessor each collectible card gets in the generated pool, keyed by card
		/// id. Exposed so the golden printer can emit a line that actually COMPILES: the tests moved to
		/// named cards, and a paste form built from a second copy of the naming rule would drift from
		/// this one the first time a reprint took a suffix.
		/// </summary>
		internal static IReadOnlyDictionary<string, string> CardAccessorsById()
		{
			// The same filter WriteNamedAccessors is handed for HSCard: collectible, minus the two
			// kinds that get their own database.
			var cards = Cards.All.Values
				.Where(c => c.Collectible && c.Type != CardType.HERO_POWER && c.Type != CardType.HERO)
				.Select(c => (Card: c, Set: c.Set))
				.ToList();

			return NameAccessors(cards)
				.ToDictionary(a => a.Card.Id, a => a.Identifier, StringComparer.Ordinal);
		}

		/// <summary>
		/// A unique C# identifier per card. Reprints share a NAME (a card and its CORE printing are
		/// both "Tuskpiercer"), so the canonical printing — lowest dbf id, the same rule
		/// <c>CardIdentity</c> uses — keeps the bare name and the others take a set suffix. Ordered
		/// by dbf id so the assignment cannot move when a later card is added.
		/// </summary>
		private static IEnumerable<(Card Card, string Identifier)> NameAccessors(
			IReadOnlyList<(Card Card, CardSet Set)> all)
		{
			var taken = new HashSet<string>(StringComparer.Ordinal);
			foreach(var (card, set) in all.OrderBy(c => c.Card.DbfId))
			{
				var name = Identifier(card.Name);
				if(name.Length == 0)
					continue;

				if(!taken.Add(name))
				{
					var suffixed = name + "_" + Identifier(set.ToString());
					// Two printings in the SAME set with the same name: fall back to the dbf id,
					// which is unique by construction. Never seen in the pool; the alternative is
					// dropping a card silently.
					if(!taken.Add(suffixed))
					{
						suffixed = name + "_" + card.DbfId.ToString(CultureInfo.InvariantCulture);
						if(!taken.Add(suffixed))
							continue;
					}
					name = suffixed;
				}

				yield return (card, name);
			}
		}

		private static string Identifier(string name)
		{
			var sb = new StringBuilder(name.Length);
			var upper = true;
			foreach(var ch in name)
			{
				if(ch >= '0' && ch <= '9')
				{
					// A leading digit is not a valid identifier start, and dropping it would make
					// "3-Card Hand" collide with "Card Hand".
					if(sb.Length == 0)
						sb.Append('_');
					sb.Append(ch);
					upper = true;
					continue;
				}

				// Non-ASCII letters (accents in localized names) are separators, not letters: an
				// identifier must be stable regardless of which locale HearthDb loaded.
				if((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
				{
					sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
					upper = false;
					continue;
				}

				upper = true;
			}

			return sb.ToString();
		}

		private static string Doc(Card card)
		{
			var stats = card.Type == CardType.MINION
				? $"{card.Cost} mana {card.Attack}/{card.Health}"
				: card.Type == CardType.WEAPON
					? $"{card.Cost} mana {card.Attack}/{card.Durability} weapon"
					: $"{card.Cost} mana {card.Type.ToString().ToLowerInvariant()}";
			var text = CardText.Flattened(card).Trim();
			if(text.Length > 120)
				text = text.Substring(0, 120) + "…";

			// XML doc is markup: an unescaped & or < in card text ("Deal 2 damage & draw") would make
			// the generated file's documentation malformed, which the compiler reports as a warning
			// — and warnings are build errors here.
			return Xml($"{card.Name} — {card.Class} {stats}." + (text.Length == 0 ? "" : $" \"{text}\""));
		}

		private static string Xml(string s) =>
			s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

		private static string SpellSchoolName(Card card) =>
			Enum.IsDefined(typeof(SpellSchool), card.SpellSchool)
				? ((SpellSchool)card.SpellSchool).ToString()
				: card.SpellSchool.ToString(CultureInfo.InvariantCulture);

		private static string Flags(Card card)
		{
			var set = FlagTags.Where(f => card.Entity.GetTag(f.Tag) != 0).Select(f => "CardFlags." + f.Name).ToList();
			return set.Count == 0 ? "CardFlags.None" : string.Join(" | ", set);
		}

		private static string FlagList(Card card)
		{
			var set = FlagTags.Where(f => card.Entity.GetTag(f.Tag) != 0).Select(f => f.Name).ToList();
			return set.Count == 0 ? "-" : string.Join(" ", set);
		}

		// A markdown cell: the pipe would end the column, and a stray CR/LF would end the row.
		// CardText.Flattened already collapses whitespace; names are not flattened at all.
		private static string Cell(string s) =>
			s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

		// A C# string literal. \r and \n cannot occur today (Flattened collapses them and no card
		// name carries one), and that is exactly why they are handled here: the one thing between
		// this generator and emitting a file that does not compile.
		private static string Escape(string s) =>
			"\"" + s
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"")
				.Replace("\r", "\\r")
				.Replace("\n", "\\n") + "\"";
	}
}
