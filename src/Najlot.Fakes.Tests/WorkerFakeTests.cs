using Najlot.Fakes.Attributes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Najlot.Fakes.Tests;

[TestClass]
public sealed class WorkerFakeTests
{
	[TestMethod]
	public async Task Abstract_Class_Fake_Supports_Overrides_Properties_And_Events()
	{
		var fake = new WorkerFake();
		var completedRaised = 0;

		fake.ExecuteReturns("done");
		Assert.AreEqual("done", fake.Execute("work"));

		fake.ExecuteAsyncResultReturns(12);
		Assert.AreEqual(12, await fake.ExecuteAsync());

		fake.Counter = 6;
		Assert.AreEqual(6, fake.Counter);

		fake.Completed += (_, _) => completedRaised++;
		fake.RaiseCompleted(fake, EventArgs.Empty);

		Assert.AreEqual(1, completedRaised);
		Assert.AreEqual(1, fake.CompletedAddCallCount);
	}
}

internal abstract class WorkerBase
{
	public abstract event EventHandler? Completed;

	public abstract int Counter { get; set; }

	public abstract string Execute(string input);

	public abstract Task<int> ExecuteAsync();
}

[Fake]
internal partial class WorkerFake : WorkerBase
{
}