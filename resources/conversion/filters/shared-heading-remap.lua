local function meta_to_string(meta_value)
  if not meta_value then
    return nil
  end

  if type(meta_value) == "string" then
    return meta_value
  end

  return pandoc.utils.stringify(meta_value)
end

local function meta_to_positive_integer(meta, key, default_value)
  local configured = meta_to_string(meta[key])
  if not configured then
    return default_value
  end

  local value = tonumber(configured)
  if not value or value < 1 then
    error(key .. " must be a positive integer metadata value")
  end

  return math.floor(value)
end

local function has_class(classes, target)
  for _, class_name in ipairs(classes or {}) do
    if class_name == target then
      return true
    end
  end
  return false
end

local function add_class(el, class_name)
  if has_class(el.classes, class_name) then
    return
  end
  el.classes[#el.classes + 1] = class_name
end

function Pandoc(doc)
  local base_level = meta_to_positive_integer(doc.meta, "heading_base_level", 1)
  local numbering_start_base = meta_to_positive_integer(doc.meta, "heading_numbering_start_base", 1)
  local heading_shift = math.floor(base_level) - 1
  local base_heading_count = 0

  if heading_shift == 0 and numbering_start_base == 1 then
    return nil
  end

  return doc:walk({
    Header = function(el)
      local source_level = el.level
      if source_level == base_level then
        base_heading_count = base_heading_count + 1
      end

      local new_level = el.level - heading_shift
      if new_level < 1 then
        return {}
      end

      el.level = new_level
      if numbering_start_base > 1 and base_heading_count < numbering_start_base then
        add_class(el, "unnumbered")
      end
      return el
    end
  })
end
