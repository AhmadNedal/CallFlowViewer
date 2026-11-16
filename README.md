# CallFlowViewer

Put the caret on any C# method in Visual Studio, press **Ctrl+.** and choose
**"Show call flow to this method"**. An HTML page opens in your browser that shows
every path in the solution that leads to that method:

- **Step by step**: pick where the flow starts (an HTTP endpoint, a test, a background
  service...) and read the flow as a short numbered story, one method per step.
  The calling code is one click away.
- **Big picture**: a diagram of every flow, from the starting points on the left to
  your method on the right. Click a box to keep only the flows that pass through it.

## Build the package

```bash
dotnet pack src/CallFlowViewer/CallFlowViewer.csproj -c Release -o ./nupkg
```

## Use it in a project

```bash
dotnet nuget add source ./nupkg --name local-callflow   # once
dotnet add package CallFlowViewer --version 0.1.0
```

The package is a development dependency (`PrivateAssets=all`). It is never copied
into your build output.

## Where it works

- Caret on a method, constructor or property **declaration** (the signature line)
- Caret on a **call**: `_orders.PlaceOrderAsync(...)` gives the flow to `PlaceOrderAsync`

## What it understands

| Situation | Behaviour |
|---|---|
| Calls through an interface (`IOrderService`) | Followed, marked *via interface* |
| Calls through a base class / `override` | Followed, marked *via base* |
| Calls inside lambdas / local functions | Attributed to the containing method, marked |
| Recursion | Detected, no infinite loops |
| Controllers, tests, hosted services, MediatR handlers, middleware, event handlers, `Main` | Recognised as starting points |

Limits: depth 25 levels, 400 methods (see `CallGraphOptions`). Calls made through
reflection, `dynamic` or framework conventions cannot be seen by static analysis.

## Debugging the extension

In a test project inside the same solution:

```xml
<ProjectReference Include="..\CallFlowViewer\CallFlowViewer.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Visual Studio caches analyzer DLLs, so restart it after you change the extension code.
Reports are written to `%TEMP%\CallFlowViewer\`.

## Requirements

Visual Studio 2022 17.8 or later (Roslyn 4.8). The graph tab loads Mermaid from
cdn.jsdelivr.net. The Paths tab works offline.
