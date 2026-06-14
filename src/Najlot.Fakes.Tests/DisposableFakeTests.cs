using Najlot.Fakes.Attributes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Najlot.Fakes.Tests;

[TestClass]
public sealed class DisposableFakeTests
{
	[TestMethod]
	public void Dispose_Fake_Tracks_Call_Count_And_Handler()
	{
		var fake = new DisposableFake();
		var disposeCalls = 0;

		fake.OnDispose(() => disposeCalls++);
		fake.Dispose();
		fake.Dispose();

		Assert.AreEqual(2, disposeCalls);
		Assert.AreEqual(2, fake.DisposeCallCount);
	}
}

[Fake]
internal partial class DisposableFake : IDisposable
{
}