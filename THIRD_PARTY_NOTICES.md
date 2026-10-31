# Third-party notices

Deskchirp's original code is copyright 2026 geekynasir and licensed under [MIT](LICENSE). Third-party software and artwork retain their own licenses and rights. This project is independent of Discord, T3 Code, Anthropic, OpenAI, Google, and other products it observes.

## Optional Discord artwork

The four PNGs in `assets/discord/` were adapted from [T3 Code](https://github.com/pingdotgg/t3code/tree/da04ebc5087477a82deae9ce19c03539a627359f) at commit `da04ebc5087477a82deae9ce19c03539a627359f`. The original paths and usage instructions are in [the artwork guide](assets/discord/README.md). The upstream MIT notice, copyright 2026 T3 Tools Inc., is preserved in [licenses/t3code-MIT.txt](licenses/t3code-MIT.txt).

Product marks identify the products being observed. The software license does not grant trademark rights or imply endorsement. Artwork is optional and disabled by default.

## Windows dependencies and bundled runtimes

Windows builds use .NET 8, Windows App SDK, and WinUI 3. Self-contained downloads bundle their runtimes. The original notices are included in `licenses/`:

- [.NET MIT license](licenses/dotnet-MIT.txt) and [runtime third-party notices](licenses/dotnet-THIRD-PARTY-NOTICES.txt), from the .NET 8 runtime source.
- [Windows App SDK MIT license](licenses/windowsappsdk-MIT.txt).
- [WinUI MIT license](licenses/winui-MIT.txt).

Microsoft Windows SDK components and system libraries remain subject to Microsoft's applicable terms. The build uses `Microsoft.Windows.SDK.BuildTools`; it is a build dependency, not an independently licensed Deskchirp component. Development tests use xUnit and the Microsoft test SDK, which are not included in the desktop downloads. Swift, AppKit, SwiftUI, and Python use the licenses and platform terms of their respective distributions.

## Documentation

The product-language guide adapts guidance from [Unslop by Damir Sokolovsky (@heydamir)](https://github.com/heydamir/unslop), under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); the attribution and modifications are recorded in that guide. Third-party license text and attributed documentation retain their original terms.
