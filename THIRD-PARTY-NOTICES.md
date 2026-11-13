# Third-party notices

RingView itself is MIT licensed (see [LICENSE](LICENSE)). It includes or is
derived from the following.

## ring-client-api

`Src/RingView.Ring` is a C# port of the parts of
[ring-client-api](https://github.com/dgreif/ring) that RingView uses (sign-in,
token rotation, devices, snapshots, live view signaling). The retired Electron
app (v0.1.x) used the package directly.

```
MIT License

Copyright (c) 2022 Dusty Greif

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## SIPSorcery

The .NET app's WebRTC comes from [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery),
Copyright (c) 2006-2026 Aaron Clauson, under the BSD 3-Clause license with an
additional use restriction. The full license, including that restriction, is at
https://github.com/sipsorcery-org/sipsorcery/blob/master/LICENSE.md and ships in
the install folder as `licenses/SIPSorcery-LICENSE.md`.

## Concentus

Opus audio decoding comes from [Concentus](https://github.com/lostromb/concentus)
under the BSD 3-Clause license; its notice ships in the install folder as
`licenses/Concentus-LICENSE.txt`.

## MIT-licensed packages

Avalonia, CommunityToolkit.Mvvm, Vortice (MediaFoundation, Direct3D11), NAudio,
Velopack and the .NET runtime libraries are MIT licensed. Their licenses are in
each package on nuget.org; `Directory.Packages.props` lists every package with
its license.
