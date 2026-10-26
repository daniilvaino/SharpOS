-- Run at every LUA start, before the script or the prompt, unless LUA_INIT
-- names something else (LUA_INIT=@file, or Lua code).
--
-- Modules are looked for along package.path: the current folder, then
-- \bin\lua (LUA_PATH replaces it). Put a file of your functions next to this
-- one — utils.lua — and `require "utils"` loads it; or require it here, and
-- every LUA has it.

require "prelude"
