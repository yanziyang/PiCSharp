# Third-Party Notices

This file records the third-party source code from which PiCSharp is derived. It supplements the
project's own `LICENSE`; it does not replace or change that licence.

## Pi Coding Agent

- Component: Pi Coding Agent (`earendil-works/pi`)
- Upstream URL: <https://github.com/earendil-works/pi>
- Version: `v0.84.4`
- Pinned commit: `b79e4cc834970cca69daebffab7df1da7d1e52c4`
- Derived directories: `src/` (the shipping C# port)
- Source notice: [`reference/pi/LICENSE`](reference/pi/LICENSE)

The following notice is reproduced verbatim from the pinned upstream `reference/pi/LICENSE`:

````text
MIT License

Copyright (c) 2025 Mario Zechner

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
````

## marked

- Component: `marked` lexer
- Upstream URL: <https://github.com/markedjs/marked>
- Version: `18.0.5`
- Pinned package integrity: `sha512-S6GcvALHg6K4ohtu4E7x0a1AqhAjp6cV8KhLSyN9qVapnzJkusVBxZRcIU9AeYsbe6P1hKDusSbEOzGyyuce6w==`
- Derived directories: `src/Pi.Tui/Marked/`
- Source notice: [`reference/marked/LICENSE`](reference/marked/LICENSE)

The following license file is reproduced verbatim from the pinned `reference/marked/LICENSE`. It
includes the marked MIT notice and the Markdown notice distributed with marked:

````text
# License information

## Contribution License Agreement

If you contribute code to this project, you are implicitly allowing your code
to be distributed under the MIT license. You are also implicitly verifying that
all code is your original work. `</legalese>`

## Marked

Copyright (c) 2018+, MarkedJS (https://github.com/markedjs/)
Copyright (c) 2011-2018, Christopher Jeffrey (https://github.com/chjj/)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## Markdown

Copyright © 2004, John Gruber
http://daringfireball.net/
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
* Neither the name “Markdown” nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

This software is provided by the copyright holders and contributors “as is” and any express or implied warranties, including, but not limited to, the implied warranties of merchantability and fitness for a particular purpose are disclaimed. In no event shall the copyright owner or contributors be liable for any direct, indirect, incidental, special, exemplary, or consequential damages (including, but not limited to, procurement of substitute goods or services; loss of use, data, or profits; or business interruption) however caused and on any theory of liability, whether in contract, strict liability, or tort (including negligence or otherwise) arising in any way out of the use of this software, even if advised of the possibility of such damage.

````

## Other ported upstream code

The current shipping source also contains the following small ports identified in
[`docs/dependencies.md` §4](docs/dependencies.md):

### `ignore` 7.0.5

- Licence: MIT
- Upstream URL: <https://github.com/kaelzhang/node-ignore>
- Derived file: `src/Pi.AgentCore/Harness/GitIgnoreMatcher.cs`

````text
Copyright (c) 2013 Kael Zhang <i@kael.me>, contributors
http://kael.me/

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
````

### `get-east-asian-width` 1.6.0

- Licence: MIT
- Upstream URL: <https://github.com/sindresorhus/get-east-asian-width>
- Derived file: `src/Pi.Tui/Text/EastAsianWidth.cs`

````text
MIT License
Copyright (c) Sindre Sorhus <sindresorhus@gmail.com> (https://sindresorhus.com)
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
````

### `typebox` 1.3.7 compatibility port

- Licence: MIT
- Upstream URL: <https://github.com/sinclairzx81/typebox>
- Derived file: `src/Pi.Ai/Utilities/TypeBoxHelpers.cs` and the provider JSON Schema helpers

````text
TypeBox

The MIT License (MIT)

Copyright (c) 2017-2026 Haydn Paterson

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
````

The other §4 entries, `proper-lockfile` 4.1.2 (MIT) and `hosted-git-info` 9.0.3 (ISC), are planned
ports only and are not present in the current shipping source. Their notices must be added here when
those ports land.

## NuGet packages

The packages declared in `Directory.Packages.props` are dependencies, not ported or vendored source:

- `AWSSDK.BedrockRuntime` 4.0.101.4 (Apache-2.0), used by the shipping `Pi.Ai` project, with
  transitive `AWSSDK.Core` 4.0.102.1.
- `xunit.v3` 4.0.0 (Apache-2.0), used only by tests and not shipped in `Pi.Cli`.

NuGet packages carry their own licence metadata and package-provided licence files. Preserve those
package notices (including notices for transitive packages) when redistributing a package or binary;
this file does not replace the package-provided Apache-2.0 notices.

## Distribution with the native AOT binary

`Pi.Cli` sets `PublishAot=true`, so a release may contain one native `pi` executable. Native AOT does
not automatically embed repository Markdown or package notice files. Every distributed binary must
therefore be accompanied by this `THIRD-PARTY-NOTICES.md`, the root `LICENSE`, and the applicable
NuGet package notices as sibling files or in an equivalent installed notices/documentation directory.
An installer or archive must install or include those files alongside the executable. This is a
distribution requirement; it does not add a publish step to CI.
