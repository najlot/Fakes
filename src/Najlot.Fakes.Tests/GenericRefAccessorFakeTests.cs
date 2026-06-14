using Najlot.Fakes.Attributes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Najlot.Fakes.Tests;

[TestClass]
public sealed class GenericRefAccessorFakeTests
{
	private static int _genericRefHandlerValue;

	[TestMethod]
	public void Generic_Ref_Return_Method_Fake_Supports_Returns_And_Handler()
	{
		var fake = new GenericRefAccessorFake();

		fake.SlotReturns<int>(10);
		ref var intSlot = ref fake.Slot<int>();

		Assert.AreEqual(10, intSlot);

		intSlot = 11;

		Assert.AreEqual(11, fake.Slot<int>());

		fake.SlotReturns<string>("first");
		ref var stringSlot = ref fake.Slot<string>();

		Assert.AreEqual("first", stringSlot);

		stringSlot = "second";

		Assert.AreEqual("second", fake.Slot<string>());

		_genericRefHandlerValue = 30;
		fake.OnSlot<int>(GetGenericRefHandlerValue);
		ref var handlerSlot = ref fake.Slot<int>();

		Assert.AreEqual(30, handlerSlot);

		handlerSlot = 31;

		Assert.AreEqual(31, _genericRefHandlerValue);
		Assert.AreEqual(5, fake.SlotCallCount);
	}

	private static ref int GetGenericRefHandlerValue()
	{
		return ref _genericRefHandlerValue;
	}
}

internal interface IGenericRefAccessor
{
	ref T Slot<T>();
}

[Fake]
internal partial class GenericRefAccessorFake : IGenericRefAccessor
{
}