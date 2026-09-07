# Components

Projects and their dependency direction. Core has no UI dependency; `CoreArchitectureTests` fails the build if a WPF, H.NotifyIcon or LiveCharts assembly is ever referenced from it.

```mermaid
flowchart TB
    APP["ClaudeTrayApp (WPF, net9.0-windows)<br/>composition root, views, viewmodels, Theme.xaml"]
    CORE["ClaudeTrayApp.Core (net9.0)<br/>domain, providers, aggregator, parsing, pricing, history"]
    TESTS["ClaudeTrayApp.Core.Tests (xunit.v3)<br/>fixtures with fake tokens only"]

    UI["WPF, H.NotifyIcon.Wpf, CommunityToolkit.Mvvm,<br/>LiveChartsCore.SkiaSharpView.WPF"]
    BCL["Microsoft.Extensions.*, Microsoft.Data.Sqlite,<br/>System.Text.Json"]

    APP --> CORE
    TESTS --> CORE
    APP --> UI
    APP --> BCL
    CORE --> BCL

    classDef forbidden stroke-dasharray: 5 5
    CORE -. "never" .-> UI
    linkStyle 5 stroke-dasharray: 5 5
```
