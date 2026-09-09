-- Number figure captions by top-level section:
-- 图 1.1 xxx, 图 1.2 xxx ... then reset on next H1.

local chapter_index = 0
local figure_index = 0
local figure_captions_enabled = true

local function has_class(classes, target)
  for _, class_name in ipairs(classes or {}) do
    if class_name == target then
      return true
    end
  end
  return false
end

local function starts_with_figure_number(text)
  if not text then
    return false
  end
  return text:match("^图%s*%d+[%.%-]%d+") ~= nil
end

local function meta_figure_captions_enabled(meta)
  local value = meta and meta.figure_captions
  if value == nil then
    return true
  end
  if type(value) == "boolean" then
    return value
  end
  if type(value) == "table" and value.t == "MetaBool" then
    return value.c
  end
  error("figure_captions must be a boolean metadata value")
end

local function make_prefix_inlines(chapter, figure)
  return {
    pandoc.Str("图"),
    pandoc.Space(),
    pandoc.Str(string.format("%d.%d", chapter, figure))
  }
end

local function prepend_prefix_to_caption(caption_long, prefix)
  if not caption_long or #caption_long == 0 then
    return { pandoc.Plain(prefix) }
  end

  local first = caption_long[1]
  if first.t == "Plain" or first.t == "Para" then
    local merged = {}
    for _, inline in ipairs(prefix) do
      merged[#merged + 1] = inline
    end
    if first.content and #first.content > 0 then
      merged[#merged + 1] = pandoc.Space()
      for _, inline in ipairs(first.content) do
        merged[#merged + 1] = inline
      end
    end
    first.content = merged
    caption_long[1] = first
    return caption_long
  end

  local updated = { pandoc.Plain(prefix) }
  for _, block in ipairs(caption_long) do
    updated[#updated + 1] = block
  end
  return updated
end

function Header(el)
  if el.level == 1 and not has_class(el.classes, "unnumbered") then
    chapter_index = chapter_index + 1
    figure_index = 0
  end
  return el
end

function Figure(el)
  if not figure_captions_enabled then
    el.caption.long = {}
    return el
  end

  local caption_text = pandoc.utils.stringify(el.caption.long or {})
  if starts_with_figure_number(caption_text) then
    return el
  end

  local chapter = chapter_index
  if chapter == 0 then
    chapter = 1
  end

  figure_index = figure_index + 1
  local prefix = make_prefix_inlines(chapter, figure_index)
  el.caption.long = prepend_prefix_to_caption(el.caption.long, prefix)
  return el
end

function Pandoc(doc)
  figure_captions_enabled = meta_figure_captions_enabled(doc.meta)
  return doc:walk({
    Header = Header,
    Figure = Figure,
  })
end
