-- Helpers for pipeline stages, so a stage stays one word:
--   SYSINFO 3>&- | LUA -e "return strings" | CONVERT --to jsonl
-- init.lua loads this file at every LUA start.

-- walk(v, f): f applied to every leaf of a tree; the tree back.
function walk(v, f)
  if type(v) == "table" then
    for k, x in pairs(v) do v[k] = walk(x, f) end
    return v
  end
  return f(v)
end

-- strings(v): every leaf as text.
function strings(v)
  return walk(v, function(x)
    if type(x) == "string" then return x end
    return tostring(x)
  end)
end

-- keyed(v): arrays as tables keyed "1", "2", ... (json2dir takes objects
-- and strings only).
function keyed(v)
  if type(v) ~= "table" then return v end
  local t = {}
  for k, x in pairs(v) do t[tostring(k)] = keyed(x) end
  return t
end
