# Contributing to SmartPipe

## Getting Started

1. Fork the repository
2. Clone your fork: `git clone https://github.com/mrfr3di/smartpipe-core.git`
3. Create a branch: `git checkout -b feature/my-feature`
4. Make your changes
5. Run tests: `dotnet test`
6. Run benchmarks: `dotnet run -c Release --project benchmarks/SmartPipe.Benchmarks`
7. Commit and push: `git push origin feature/my-feature`
8. Open a Pull Request

## Pull Request Guidelines

- Keep PRs small and focused on a single feature or fix
- Add tests for new functionality
- Update documentation if API changes
- Ensure all tests pass before submitting
- Run benchmarks to verify no performance regressions

## Code Style

- Use C# 12+ features where appropriate
- Follow Microsoft's C# coding conventions
- Add XML documentation comments for public APIs
- Use `ConfigureAwait(false)` in library code

## Reporting Bugs

Open an issue with:
- Description of the problem
- Steps to reproduce
- Expected behavior
- Actual behavior
- Environment: .NET version, OS, package version

## Feature Requests

Open an issue with:
- Clear description of the feature
- Use case: why you need it
- Proposed API (if applicable)