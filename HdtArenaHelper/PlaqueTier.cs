namespace HdtArenaHelper
{
	/// <summary>
	/// Maps our 0-100 blended score onto HDT's 1-5 arena plaque tier (the flame/bolt
	/// intensity of <c>ArenaPlaqueViewModel.Level</c>). Kept WPF-free so it stays unit
	/// testable without a live presentation stack.
	/// </summary>
	internal static class PlaqueTier
	{
		internal static int FromScore(double score) => score switch
		{
			>= 80 => 5,
			>= 65 => 4,
			>= 50 => 3,
			>= 40 => 2,
			_ => 1,
		};
	}
}
