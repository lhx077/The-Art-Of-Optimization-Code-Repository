# The Art of Optimization: A Deep Dive into the Peak of .NET Performance — Practice

Companion exercise repository for *The Art of Optimization: A Deep Dive into the Peak of .NET Performance*, covering Chapters 2–25 with 131 hands-on exercises and 24 chapters of thought question answers.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Recommended IDE: Visual Studio 2022 17.14+ / JetBrains Rider 2025.1+ / VS Code + C# Dev Kit

## Getting Started

```bash
# Clone the repository
git clone <repo-url>
cd Practice

# Build all projects
dotnet build

# Run an exercise (e.g. Chapter 2, Exercise 1)
dotnet run --project src/Chapter02/Exercise01.BenchmarkBasics

# Run benchmarks (supported by select exercises)
dotnet run --project src/Chapter02/Exercise01.BenchmarkBasics -c Release -- --benchmark
```

## Project Structure

```
Practice/
├── src/
│   ├── Chapter02/                    # Benchmarking, Profiling, and Performance Measurement
│   ├── Chapter03/                    # CPU Architecture and Hardware-Level Optimization
│   ├── Chapter04/                    # Value Types, Memory Layout, and Struct Design
│   ├── Chapter05/                    # High-Performance String Processing
│   ├── Chapter06/                    # Collection Types, LINQ, and Algorithm Selection
│   ├── Chapter07/                    # Delegates, Reflection, and Metaprogramming Performance
│   ├── Chapter08/                    # .NET GC Deep Dive
│   ├── Chapter09/                    # Identifying and Eliminating Unnecessary Allocations
│   ├── Chapter10/                    # Span<T>, Memory<T>, and High-Performance I/O
│   ├── Chapter11/                    # IDisposable, Finalizers, and Resource Management
│   ├── Chapter12/                    # System.IO.Pipelines: High-Performance I/O
│   ├── Chapter13/                    # The Inner Workings of the JIT Compiler
│   ├── Chapter14/                    # unsafe Code and Pointers
│   ├── Chapter15/                    # SIMD and Hardware Intrinsics
│   ├── Chapter16/                    # Bit Manipulation Optimization
│   ├── Chapter17/                    # Memory Alignment and Data Layout
│   ├── Chapter18/                    # Lock-Free Programming and Atomic Operations
│   ├── Chapter19/                    # Async Programming Performance Optimization
│   ├── Chapter20/                    # Parallel Programming and Dataflow
│   ├── Chapter21/                    # Memory Visibility and Synchronization Primitives
│   ├── Chapter22/                    # ASP.NET Core Performance Optimization
│   ├── Chapter23/                    # Entity Framework Core Performance Optimization
│   ├── Chapter24/                    # Source Generators and Native AOT
│   └── Chapter25/                    # Performance Engineering Practices and Team Collaboration
├── docs/
│   └── Chapter*_ThoughtQuestions.txt  # Thought question answers (24 chapters)
├── Directory.Build.props             # Global build configuration (net10.0)
└── TheArtOfOptimization.Practice.sln # Solution file
```

## Chapter Overview

| Chapter | Topic | Exercises |
|---------|-------|-----------|
| 02 | Benchmarking, Profiling, and Performance Measurement | 7 |
| 03 | CPU Architecture and Hardware-Level Optimization | 7 |
| 04 | Value Types, Memory Layout, and Struct Design | 5 |
| 05 | High-Performance String Processing | 5 |
| 06 | Collection Types, LINQ, and Algorithm Selection | 5 |
| 07 | Delegates, Reflection, and Metaprogramming Performance | 5 |
| 08 | .NET GC Deep Dive | 6 |
| 09 | Identifying and Eliminating Unnecessary Allocations | 5 |
| 10 | Span\<T\>, Memory\<T\>, and High-Performance I/O | 5 |
| 11 | IDisposable, Finalizers, and Resource Management | 5 |
| 12 | System.IO.Pipelines: High-Performance I/O | 5 |
| 13 | The Inner Workings of the JIT Compiler | 6 |
| 14 | unsafe Code and Pointers | 5 |
| 15 | SIMD and Hardware Intrinsics | 5 |
| 16 | Bit Manipulation Optimization | 5 |
| 17 | Memory Alignment and Data Layout | 5 |
| 18 | Lock-Free Programming and Atomic Operations | 6 |
| 19 | Async Programming Performance Optimization | 8 |
| 20 | Parallel Programming and Dataflow | 6 |
| 21 | Memory Visibility and Synchronization Primitives | 5 |
| 22 | ASP.NET Core Performance Optimization | 5 |
| 23 | Entity Framework Core Performance Optimization | 5 |
| 24 | Source Generators and Native AOT | 5 |
| 25 | Performance Engineering Practices and Team Collaboration | 5 |

## Running Exercises

Each exercise is a standalone console application. Run it directly with `dotnet run` for demo mode. Some exercises support BenchmarkDotNet benchmarks:

```bash
# Demo mode (quick preview)
dotnet run --project src/Chapter13/Exercise02.BoundsCheckElimination

# Benchmark mode (requires Release configuration)
dotnet run --project src/Chapter13/Exercise02.BoundsCheckElimination -c Release -- --benchmark
```

Exercises that require the ASP.NET Core runtime (Chapter 22) will start a local web server.

## Thought Questions

The `docs/` directory contains reference answers for each chapter's thought questions, one `.txt` file per chapter, in a consistent format:

```
================================================================================
Chapter NN — [Chapter Title]: Thought Question Answers
================================================================================

Question 1: [Question text]
--------------------------------------------------------------------------------

[Reference answer]
```

## Tech Stack

- **.NET 10** (Preview)
- **BenchmarkDotNet** — Benchmarking framework
- **Microsoft.CodeAnalysis** — Roslyn API / Source Generators
- **System.IO.Pipelines** — High-performance I/O
- **System.Threading.Channels** — Async message passing
- **TPL Dataflow** — Dataflow parallel processing
- **Entity Framework Core** — ORM performance analysis
- **ASP.NET Core** — Web performance optimization

## License

MIT License
