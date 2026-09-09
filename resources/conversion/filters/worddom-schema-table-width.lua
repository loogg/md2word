-- Generic table optimizer for industrial manuals.
-- Strategy:
-- 1) No header keyword matching and no per-table hardcoded ratios.
-- 2) Use content-driven "column roles" to compute widths.
-- 3) Protected columns use max-content-driven width floors.

local function trim(text)
  return (text:gsub("^%s+", ""):gsub("%s+$", ""))
end

local function normalize(text)
  local s = trim(text or "")
  s = s:gsub("%s+", " ")
  return s
end

local function col_width_value(value)
  if pandoc.ColWidth then
    return pandoc.ColWidth(value)
  end
  return value
end

local function set_widths(tbl, widths)
  for i, w in ipairs(widths) do
    if tbl.colspecs[i] then
      tbl.colspecs[i] = { tbl.colspecs[i][1], col_width_value(w) }
    end
  end
end

local function append_class(el, class_name)
  if not (el and el.attr and el.attr.classes) then
    return
  end
  for _, c in ipairs(el.attr.classes) do
    if c == class_name then
      return
    end
  end
  el.attr.classes:insert(class_name)
end

local function stringify_simple_cell(cell_blocks)
  return normalize(pandoc.utils.stringify(cell_blocks or {}))
end

local function has_tree_marker(text)
  if not text or text == "" then
    return false
  end
  -- Unicode tree drawing characters.
  if text:find("[├└│─]") then
    return true
  end
  -- ASCII fallback patterns like |-- key or +-- key.
  if text:find("%-%-") and text:match("^%s*[%|`+%-]+%s*[%w_]") then
    return true
  end
  return false
end

local function is_numeric_like(text)
  if not text or text == "" then
    return false
  end
  if text:match("^%s*[-+]?[%d%.]+%s*$") then
    return true
  end
  if text:match("^%s*[%d%.%-%+/%s~～]+%s*$") and text:find("%d") then
    return true
  end
  return false
end

local function is_code_like(text)
  if not text or text == "" then
    return false
  end
  return text:match("^[A-Za-z0-9_]+$") ~= nil
end

local function median(values)
  if #values == 0 then
    return 0
  end
  table.sort(values)
  local mid = math.floor((#values + 1) / 2)
  if #values % 2 == 1 then
    return values[mid]
  end
  return (values[mid] + values[mid + 1]) / 2
end

local function collect_column_stats(simple_tbl)
  local col_count = #(simple_tbl.headers or {})
  if col_count < 2 then
    return nil
  end

  local rows = simple_tbl.rows or {}
  local stats = {}
  for i = 1, col_count do
    local header_text = stringify_simple_cell((simple_tbl.headers or {})[i] or {})
    stats[i] = {
      header_len = #header_text,
      non_empty = 0,
      len_sum = 0,
      avg_len = 0,
      max_len = 0,
      short_count = 0,
      tree_count = 0,
      numeric_count = 0,
      code_count = 0,
      space_count = 0,
      short_ratio = 0,
      tree_ratio = 0,
      numeric_ratio = 0,
      code_ratio = 0,
      space_ratio = 0
    }
  end

  for _, row in ipairs(rows) do
    for i = 1, col_count do
      local cell_text = stringify_simple_cell(row[i])
      if cell_text ~= "" then
        local s = stats[i]
        local len = #cell_text
        s.non_empty = s.non_empty + 1
        s.len_sum = s.len_sum + len
        if len > s.max_len then
          s.max_len = len
        end
        if len <= 18 then
          s.short_count = s.short_count + 1
        end
        if has_tree_marker(cell_text) then
          s.tree_count = s.tree_count + 1
        end
        if is_numeric_like(cell_text) then
          s.numeric_count = s.numeric_count + 1
        end
        if is_code_like(cell_text) then
          s.code_count = s.code_count + 1
        end
        if cell_text:find("%s") then
          s.space_count = s.space_count + 1
        end
      end
    end
  end

  -- If a column has no body content, use header length as minimal signal.
  for i = 1, col_count do
    local s = stats[i]
    if s.non_empty == 0 then
      local header_text = stringify_simple_cell((simple_tbl.headers or {})[i] or {})
      if header_text ~= "" then
        s.non_empty = 1
        s.len_sum = #header_text
        s.max_len = #header_text
        s.short_count = (#header_text <= 18) and 1 or 0
        s.code_count = is_code_like(header_text) and 1 or 0
        s.numeric_count = is_numeric_like(header_text) and 1 or 0
        s.tree_count = has_tree_marker(header_text) and 1 or 0
        s.space_count = header_text:find("%s") and 1 or 0
      end
    end
  end

  local avg_values = {}
  for i = 1, col_count do
    local s = stats[i]
    if s.non_empty > 0 then
      s.avg_len = s.len_sum / s.non_empty
      s.short_ratio = s.short_count / s.non_empty
      s.tree_ratio = s.tree_count / s.non_empty
      s.numeric_ratio = s.numeric_count / s.non_empty
      s.code_ratio = s.code_count / s.non_empty
      s.space_ratio = s.space_count / s.non_empty
    else
      s.avg_len = 0
    end
    if s.avg_len > 0 then
      avg_values[#avg_values + 1] = s.avg_len
    end
  end

  return {
    row_count = #rows,
    col_count = col_count,
    stats = stats,
    median_avg = median(avg_values)
  }
end

local function is_key_column(col_stat, median_avg)
  local med = math.max(median_avg or 10, 1)
  local shortish = col_stat.avg_len <= math.max(18, med * 1.1) and col_stat.short_ratio >= 0.55
  local codeish = col_stat.code_ratio >= 0.5 or col_stat.numeric_ratio >= 0.45
  return shortish and codeish
end

local function detect_key_columns(table_stats)
  local result = {}
  local med = table_stats.median_avg > 0 and table_stats.median_avg or 10
  for i, s in ipairs(table_stats.stats) do
    result[i] = is_key_column(s, med)
  end
  return result
end

local function detect_tight_columns(table_stats, key_cols)
  local result = {}
  local col_count = table_stats.col_count
  local med = table_stats.median_avg > 0 and table_stats.median_avg or 10
  for i, s in ipairs(table_stats.stats) do
    result[i] = false
    if i > 1 and i < col_count then
      local compactish = s.avg_len <= math.max(18, med * 1.15) and s.short_ratio >= 0.55
      local header_short = s.header_len > 0 and s.header_len <= 12
      local keyish = (key_cols[i] == true) or s.numeric_ratio >= 0.18 or s.code_ratio >= 0.22
      local low_wrap_friendly = s.space_ratio <= 0.28 or s.numeric_ratio >= 0.35 or s.code_ratio >= 0.35
      result[i] = compactish and low_wrap_friendly and (keyish or header_short)
    end
  end
  return result
end

local function is_tree_schema_table(table_stats)
  local first = table_stats.stats[1]
  if not first then
    return false
  end
  return first.tree_count >= 2 and first.tree_ratio >= 0.12
end

local function clamp(v, min_v, max_v)
  if v < min_v then
    return min_v
  end
  if v > max_v then
    return max_v
  end
  return v
end

local function apply_width_limits(widths, min_widths, max_w)
  local n = #widths
  if n == 0 then
    return widths
  end

  local resolved_mins = {}
  local min_total = 0
  for i = 1, n do
    local min_w = min_widths
    if type(min_widths) == "table" then
      min_w = min_widths[i] or 0
    end
    min_w = min_w or 0
    resolved_mins[i] = min_w
    min_total = min_total + min_w
  end

  if min_total > 1 then
    local scale = 1 / min_total
    for i = 1, n do
      resolved_mins[i] = resolved_mins[i] * scale
    end
  end

  local fixed = {}
  local out = {}
  local remain = 1.0
  for i = 1, n do
    fixed[i] = false
    out[i] = widths[i]
  end

  local changed = true
  while changed do
    changed = false

    local sum_free = 0
    for i = 1, n do
      if not fixed[i] then
        sum_free = sum_free + out[i]
      end
    end

    if sum_free <= 0 then
      break
    end

    for i = 1, n do
      if not fixed[i] then
        local min_w = resolved_mins[i]
        if max_w < min_w then
          min_w = max_w
        end
        local scaled = (out[i] / sum_free) * remain
        if scaled < min_w then
          out[i] = min_w
          fixed[i] = true
          remain = remain - min_w
          changed = true
        elseif scaled > max_w then
          out[i] = max_w
          fixed[i] = true
          remain = remain - max_w
          changed = true
        else
          out[i] = scaled
        end
      end
    end
  end

  local sum_free = 0
  for i = 1, n do
    if not fixed[i] then
      sum_free = sum_free + out[i]
    end
  end

  if sum_free > 0 then
    for i = 1, n do
      if not fixed[i] then
        out[i] = (out[i] / sum_free) * remain
      end
    end
  else
    local share = remain / n
    for i = 1, n do
      if not fixed[i] then
        out[i] = share
      end
    end
  end

  local total = 0
  for i = 1, n do
    total = total + out[i]
  end
  out[n] = out[n] + (1 - total)

  return out
end

local function normalize_weights(weights)
  local sum = 0
  for _, w in ipairs(weights) do
    sum = sum + w
  end
  if sum <= 0 then
    local even = 1 / #weights
    local out = {}
    for i = 1, #weights do
      out[i] = even
    end
    return out
  end
  local out = {}
  for i, w in ipairs(weights) do
    out[i] = w / sum
  end
  return out
end

local function build_tree_first_widths(table_stats)
  local col_count = table_stats.col_count
  local first = table_stats.stats[1]
  local first_avg = first.avg_len
  local first_max = first.max_len

  local other_avg_sum = 0
  local other_count = 0
  for i = 2, col_count do
    if table_stats.stats[i].avg_len > 0 then
      other_avg_sum = other_avg_sum + table_stats.stats[i].avg_len
      other_count = other_count + 1
    end
  end
  local other_avg = (other_count > 0) and (other_avg_sum / other_count) or math.max(first_avg, 1)
  local balance = first_avg / math.max(first_avg + other_avg, 1)
  local first_width = 0.27 + (balance * 0.16)
  if first_max > 24 then
    first_width = first_width + 0.02
  end
  if col_count >= 4 then
    first_width = first_width - 0.02
  end
  local max_first = 0.38
  if col_count == 4 then
    max_first = 0.34
  elseif col_count >= 5 then
    max_first = 0.32
  end
  first_width = clamp(first_width, 0.24, max_first)

  local widths = { first_width }
  local rest = (1 - first_width) / (col_count - 1)
  for i = 2, col_count do
    widths[i] = rest
  end
  return widths
end

local function build_two_col_widths(table_stats)
  local first_width = 0.24
  return { first_width, 1 - first_width }
end

local function build_weighted_widths(table_stats, key_cols, tight_cols)
  local col_count = table_stats.col_count
  local stats = table_stats.stats
  local med = table_stats.median_avg > 0 and table_stats.median_avg or 10

  local weights = {}
  for i = 1, col_count do
    local s = stats[i]
    local w = 1.0

    local len_factor = clamp(s.avg_len / med, 0.75, 1.8)
    w = w * len_factor

    if key_cols[i] then
      w = w * ((tight_cols and tight_cols[i]) and 0.98 or 0.92)
    end
    if i == 1 then
      w = w * 0.98
    end
    if tight_cols and tight_cols[i] then
      w = w * 1.06
    end

    weights[i] = w
  end

  local widths = normalize_weights(weights)
  local base_min = (col_count >= 5) and 0.10 or 0.12
  local min_widths = {}
  for i = 1, col_count do
    local min_w = base_min
    if i == 1 then
      min_w = math.max(min_w, 0.15)
    end
    if key_cols[i] then
      min_w = math.max(min_w, 0.14)
    end
    if tight_cols and tight_cols[i] then
      min_w = math.max(min_w, 0.16)
    end
    min_widths[i] = min_w
  end
  local max_w = (col_count >= 5) and 0.44 or 0.52
  return apply_width_limits(widths, min_widths, max_w)
end

local function annotate_columns(tbl, key_cols, tight_cols)
  local function mark_row(row)
    local col_idx = 1
    for _, cell in ipairs(row.cells or {}) do
      if key_cols[col_idx] then
        append_class(cell, "col-key")
      end
      if col_idx == 1 and key_cols[col_idx] then
        append_class(cell, "col-first")
      end
      if tight_cols and tight_cols[col_idx] then
        append_class(cell, "col-tight")
      end
      col_idx = col_idx + (cell.col_span or 1)
    end
  end

  for _, row in ipairs((tbl.head and tbl.head.rows) or {}) do
    mark_row(row)
  end
  for _, body in ipairs(tbl.bodies or {}) do
    for _, row in ipairs(body.head or {}) do
      mark_row(row)
    end
    for _, row in ipairs(body.body or {}) do
      mark_row(row)
    end
  end
  for _, row in ipairs((tbl.foot and tbl.foot.rows) or {}) do
    mark_row(row)
  end
end

function Table(tbl)
  local ok, simple = pcall(pandoc.utils.to_simple_table, tbl)
  if not ok or not simple then
    return tbl
  end

  local ts = collect_column_stats(simple)
  if not ts then
    return tbl
  end

  local widths
  local key_cols = detect_key_columns(ts)
  local tight_cols = nil

  if ts.col_count == 2 then
    widths = build_two_col_widths(ts)
    key_cols = { false, false }
  else
    tight_cols = detect_tight_columns(ts, key_cols)
    widths = build_weighted_widths(ts, key_cols, tight_cols)
  end

  set_widths(tbl, widths)
  annotate_columns(tbl, key_cols, tight_cols)
  return tbl
end
