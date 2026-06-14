using Najlot.Fakes.Attributes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Najlot.Fakes.Tests;

[TestClass]
public sealed class CalculatorFakeTests
{
	[TestMethod]
	public void Property_Fake_Tracks_Get_Set_And_Returns()
	{
		var fake = new CalculatorFake();
		string? lastAssignedValue = null;

		fake.OnSetName(value => lastAssignedValue = value);
		fake.Name = "first";

		Assert.AreEqual("first", lastAssignedValue);
		Assert.AreEqual(1, fake.NameSetCallCount);
		Assert.AreEqual("first", fake.Name);

		fake.OnGetName(() => "hooked");

		Assert.AreEqual("hooked", fake.Name);
		Assert.AreEqual(2, fake.NameGetCallCount);

		fake.NameReturns("returned");

		Assert.AreEqual("returned", fake.Name);
	}

	[TestMethod]
	public async Task Method_Fake_Supports_Return_Handlers_And_Async_Methods()
	{
		var fake = new CalculatorFake();

		fake.AddReturns(42);
		Assert.AreEqual(42, fake.Add(1, 2));
		Assert.AreEqual(1, fake.AddCallCount);

		fake.AddReturns(7);
		fake.AddReturns(20, 22, 42);
		fake.AddReturns(7, 8, 15);
		fake.AddReturns(7, 8, 9, 24);

		Assert.AreEqual(42, fake.Add(20, 22));
		Assert.AreEqual(15, fake.Add(7, 8));
		Assert.AreEqual(7, fake.Add(3, 4));
		Assert.AreEqual(24, fake.Add(7, 8, 9));

		fake.OnAdd((left, right) => left + right);
		Assert.AreEqual(7, fake.Add(3, 4));

		Assert.AreEqual(6, fake.AddCallCount);

		fake.LoadAsyncResultReturns("payload");
		Assert.AreEqual("payload", await fake.LoadAsync(5, CancellationToken.None));

		fake.CountAsyncResultReturns(9);
		Assert.AreEqual(9, await fake.CountAsync());

		fake.OnFlushAsync(_ => Task.CompletedTask);
		await fake.FlushAsync(CancellationToken.None);

		Assert.AreEqual(1, fake.FlushAsyncCallCount);
	}
}

internal interface ICalculator
{
	int Add(int left, int right);

	int Add(int left, int right, int add);

	string Name { get; set; }

	Task<string> LoadAsync(int id, CancellationToken cancellationToken);

	ValueTask<int> CountAsync();

	Task FlushAsync(CancellationToken cancellationToken);
}

[Fake]
internal partial class CalculatorFake : ICalculator
{
}