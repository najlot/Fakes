# Contributing to Fakes

First off, thank you for considering contributing to Fakes! It's people like you that make open source such a great community.

## Getting Started

1. Fork the repository and create your branch from `main`.
2. Ensure you have the [.NET SDK](https://dotnet.microsoft.com/download) (or later) installed.
3. Open `src/Fakes.slnx` in Visual Studio or your preferred IDE.

## Building and Testing

You can build the solution from the command line:

```powershell
dotnet build src/Fakes.slnx
```

And run the tests:

```powershell
dotnet test src/Fakes.Tests/Fakes.Tests.csproj
```

**Before submitting a pull request**, please ensure that:
- All tests pass.
- If you're adding a new feature, please include tests that cover it.
- If you're fixing a bug, please add a test that would fail without your fix.

## Code Style

This project uses an `.editorconfig` file to enforce coding standards. Most IDEs will automatically format your code according to these rules. Please ensure your code adheres to these guidelines before submitting a PR.
