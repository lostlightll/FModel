# Lua opcode provenance

The instruction metadata (47 Lua 5.3 opcodes, operand modes and bit fields) is derived from Lua 5.3.6 `src/lopcodes.h` and `src/lopcodes.c`:
https://www.lua.org/ftp/lua-5.3.6.tar.gz
https://www.lua.org/source/5.3/lopcodes.h.html
https://www.lua.org/source/5.3/lopcodes.c.html

The `slua53` opcode permutation is from Tencent/sluaunreal, commit `7970ab72fd59118e1b3f86aac2d016828b54b748`, `Plugins/slua_unreal/External/lua/lopcodes.h` and `lopcodes.cpp`. It is an explicit mapping, not inferred from a chunk header:
https://github.com/Tencent/sluaunreal/tree/7970ab72fd59118e1b3f86aac2d016828b54b748

The bundled Lua 5.3.4 and Tencent modifications use MIT, as listed in [LICENSE.TXT](https://github.com/Tencent/sluaunreal/blob/7970ab72fd59118e1b3f86aac2d016828b54b748/LICENSE.TXT). Lua 5.3.4 copyright: Copyright (C) 1994-2017 Lua.org, PUC-Rio. Tencent modifications: Copyright (C) 2018 THL A29 Limited. The MIT permission and disclaimer below apply to this adapted metadata as well as the Lua 5.3.6 metadata.

Only opcode metadata is adapted here. The chunk reader and validation logic are newly implemented in C#. Standard Lua header bytes do not identify the opcode permutation. Auto detection validates every prototype against both supported mappings and refuses zero or multiple matches. Validation is structural, not a claim that arbitrary bytecode is safe to execute; the CLI never executes it or claims to recover original source.

Lua 5.3 license (MIT):

Copyright (C) 1994-2020 Lua.org, PUC-Rio.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

