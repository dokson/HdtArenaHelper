using System.Collections.Generic;
using HearthDb.Enums;

namespace HdtArenaHelper
{
	/// <summary>
	/// Collapses a card's REPRINTS onto one identity so the win-rate feeds can be joined: HSReplay
	/// reports Illidari Studies as <c>CORE_YOP_001</c>, Firestone as <c>YOP_001</c>, and joining on the
	/// raw dbf id left 216 cards with a single source — where the consensus is worth most.
	///
	/// Matched by NAME, measured: id normalisation recovered 210 of those 216 (printings follow no
	/// single pattern — <c>CORE_EDR_004</c> vs <c>CORE_EDR_004_2026</c>), the name recovered all 216
	/// with zero ambiguous groups. Guarded because a name is a weaker key: COLLECTIBLE cards only
	/// (tokens reuse names and are never drafted), keyed with class and type. Anything unmapped keeps
	/// its own id — failing to merge costs a consensus, a wrong merge pools two cards' win-rates.
	/// </summary>
	internal static class CardIdentity
	{
		private static volatile Dictionary<int, int>? _canonical;
		private static readonly object InitLock = new object();

		/// <summary>
		/// The dbf id every printing of this card maps to, or <paramref name="dbfId"/> itself when it
		/// has no other printing (or HearthDb is not loaded yet — the map is built lazily for exactly
		/// that reason: HearthDb is empty while HDT starts).
		/// </summary>
		internal static int Canonical(int dbfId)
		{
			var map = ResolveMap();
			return map != null && map.TryGetValue(dbfId, out var canonical) ? canonical : dbfId;
		}

		/// <summary>Drops the cached map; for tests, and harmless at runtime.</summary>
		internal static void Reset() => _canonical = null;

		private static Dictionary<int, int>? ResolveMap()
		{
			var map = _canonical;
			if(map != null)
				return map;

			lock(InitLock)
			{
				if(_canonical != null)
					return _canonical;
				if(HearthDb.Cards.All.Count == 0)
					return null; // HearthDb not ready; try again on the next call

				// Group collectible printings by (name, class, type); the LOWEST dbf id wins as the
				// canonical one. Lowest rather than newest only because it must be deterministic —
				// nothing downstream cares which printing is chosen, only that both feeds and the
				// client agree on one.
				var byKey = new Dictionary<(string, CardClass, CardType), int>();
				foreach(var card in HearthDb.Cards.All.Values)
				{
					if(!card.Collectible || card.DbfId == 0 || string.IsNullOrEmpty(card.Name))
						continue;
					var key = (card.Name, card.Class, card.Type);
					if(!byKey.TryGetValue(key, out var seen) || card.DbfId < seen)
						byKey[key] = card.DbfId;
				}

				var canonical = new Dictionary<int, int>();
				foreach(var card in HearthDb.Cards.All.Values)
				{
					if(!card.Collectible || card.DbfId == 0 || string.IsNullOrEmpty(card.Name))
						continue;
					if(byKey.TryGetValue((card.Name, card.Class, card.Type), out var target))
						canonical[card.DbfId] = target;
				}
				_canonical = canonical;
				return canonical;
			}
		}
	}
}
