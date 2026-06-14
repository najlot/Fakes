# Fakes

Fakes is a Roslyn source generator for test doubles.

Annotate a `partial` class with `[Fake]` and the generator emits implementations for the interfaces and abstract members that the type still needs for unit tests.

## Projects

- `src/Najlot.Fakes.Abstractions`: shared marker attribute package containing `Najlot.Fakes.Attributes.FakeAttribute`
- `src/Najlot.Fakes`: incremental source generator that emits fake implementations
- `src/Najlot.Fakes.Tests`: MSTest coverage for generated behavior

## Usage

The package is distributed as a NuGet package.

```powershell
dotnet add package Najlot.Fakes --version 1.0.0
```

Then declare a fake target as a `partial` class.

```csharp
using Najlot.Fakes.Attributes;

[Fake]
internal partial class MyDisposable : IDisposable
{
}
```

The generator emits the missing implementation and test hooks such as:

```csharp
var fake = new MyDisposable();
var disposeCalls = 0;

fake.OnDispose(() => disposeCalls++);
fake.Dispose();

Assert.AreEqual(1, disposeCalls);
Assert.AreEqual(1, fake.DisposeCallCount);
```

## Generated API Pattern

The emitted API is based on the inherited member name.

- Methods get `OnX(...)`, `XCallCount`, and `XReturns(...)` for return values.
- Methods with comparable input parameters also get `XReturns(..., value)` overloads for parameter-specific return values.
- `Task<T>` and `ValueTask<T>` methods also get `XResultReturns(...)` helpers that wrap the supplied result.
- Properties get `OnGetX(...)`, `OnSetX(...)`, `XGetCallCount`, `XSetCallCount`, and `XReturns(...)` for getter results.
- Events get add/remove call counts and a `RaiseX(...)` helper.

Example:

```csharp
internal interface ICalculator
{
		int Add(int left, int right);
		string Name { get; set; }
		Task<string> LoadAsync(int id, CancellationToken cancellationToken);
}

[Fake]
internal partial class CalculatorFake : ICalculator
{
}

var fake = new CalculatorFake();

fake.AddReturns(42);
fake.AddReturns(20, 22, 42);
fake.OnSetName(value => Console.WriteLine(value));
fake.LoadAsyncResultReturns("payload");
```

## Supported Targets

- Interface implementations
- Abstract class overrides
- Methods with synchronous return values
- Generic methods, including ref-returning generic methods
- `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` members
- Properties and indexers
- Events
- Nested fake types when the containing types are also `partial`

## Constraints

The generator is intentionally strict and reports diagnostics when it cannot safely emit a fake.

- Fake targets must be `partial` classes or record classes.
- File-local types are not supported.
- Pointer and function-pointer signatures are not supported.
- Ref-returning properties and indexers are not supported.
- If no interface or abstract members need implementation, generation fails with a diagnostic instead of emitting an empty fake.

Current diagnostics:

- `FAKE001`: fake type must be `partial`
- `FAKE002`: unsupported fake target type
- `FAKE003`: containing type must be `partial`
- `FAKE004`: no fakeable members were found
- `FAKE005`: inherited member is not supported
- `FAKE006`: inherited members conflict
