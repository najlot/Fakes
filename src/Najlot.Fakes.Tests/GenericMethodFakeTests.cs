using Najlot.Fakes.Attributes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Najlot.Fakes.Tests;

[TestClass]
public sealed class GenericMethodFakeTests
{
	[TestMethod]
	public void Generic_Method_Fake_Supports_Returns_And_Handler_Maps()
	{
		var fake = new PermissionServiceFake();
		var expected = new[] { 1, 2, 3 }.AsQueryable();

		fake.ApplyReadFilterReturns<int>(expected);

		Assert.AreSame(expected, fake.ApplyReadFilter(Array.Empty<int>().AsQueryable()));
		Assert.AreEqual(1, fake.ApplyReadFilterCallCount);

		fake.OnCanAccess<string>(item => item.Length > 0);

		Assert.IsTrue(fake.CanAccess("allowed"));
		Assert.IsFalse(fake.CanAccess(string.Empty));
		Assert.AreEqual(2, fake.CanAccessCallCount);
	}

	[TestMethod]
	public async Task Constrained_Generic_Task_Method_Fake_Supports_Returns_And_Handler_Maps()
	{
		var fake = new PublisherFake();
		var completed = Task.CompletedTask;
		string? capturedUserId = null;
		string? capturedMessage = null;

		fake.PublishAsyncReturns<object>(completed);

		Assert.AreSame(completed, fake.PublishAsync<object>(new object()));
		Assert.AreEqual(1, fake.PublishAsyncCallCount);

		fake.OnPublishToUserAsync<string>((userId, message) =>
		{
			capturedUserId = userId;
			capturedMessage = message;
			return Task.CompletedTask;
		});

		await fake.PublishToUserAsync("user-1", "payload");

		Assert.AreEqual("user-1", capturedUserId);
		Assert.AreEqual("payload", capturedMessage);
		Assert.AreEqual(1, fake.PublishToUserAsyncCallCount);
	}
}

internal interface IPermissionService
{
	IQueryable<T> ApplyReadFilter<T>(IQueryable<T> query);

	bool CanAccess<T>(T item);
}

[Fake]
internal partial class PermissionServiceFake : IPermissionService
{
}

internal interface IPublisher
{
	Task PublishAsync<T>(T message) where T : notnull;

	Task PublishToUserAsync<T>(string userId, T message) where T : notnull;
}

[Fake]
internal partial class PublisherFake : IPublisher
{
}
