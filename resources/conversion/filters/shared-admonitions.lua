-- Convert quote blocks with NOTE/WARNING style prefixes into semantic
-- admonition divs without changing the source markdown file.

local keyword_rules = {
  { key = "DANGER", zh = false, class = "admonition-danger", label = "DANGER" },
  { key = "危险", zh = true, class = "admonition-danger", label = "危险" },
  { key = "WARNING", zh = false, class = "admonition-warning", label = "WARNING" },
  { key = "警告", zh = true, class = "admonition-warning", label = "警告" },
  { key = "CAUTION", zh = false, class = "admonition-caution", label = "CAUTION" },
  { key = "注意", zh = true, class = "admonition-caution", label = "注意" },
  { key = "NOTE", zh = false, class = "admonition-note", label = "NOTE" },
  { key = "说明", zh = true, class = "admonition-note", label = "说明" },
  { key = "提示", zh = true, class = "admonition-note", label = "提示" }
}

local function trim(text)
  return (text:gsub("^%s+", ""):gsub("%s+$", ""))
end

local function text_to_inlines(text)
  local inlines = {}
  local has_token = false
  for token in text:gmatch("%S+") do
    if has_token then
      inlines[#inlines + 1] = pandoc.Space()
    end
    inlines[#inlines + 1] = pandoc.Str(token)
    has_token = true
  end
  return inlines
end

local function match_rule(text)
  local candidate = trim(text)
  for _, rule in ipairs(keyword_rules) do
    local lhs = candidate
    local key = rule.key
    if not rule.zh then
      lhs = lhs:upper()
    end
    if lhs:sub(1, #key) == key then
      local suffix = candidate:sub(#rule.key + 1)
      local body = suffix:match("^%s*[：:]%s*(.*)$")
      if body ~= nil then
        return rule, trim(body)
      end
    end
  end
  return nil, nil
end

function BlockQuote(el)
  if not el.content or #el.content == 0 then
    return nil
  end

  local first_block = el.content[1]
  if first_block.t ~= "Para" and first_block.t ~= "Plain" then
    return pandoc.Div(el.content, pandoc.Attr("", { "admonition" }, {}))
  end

  local raw_text = pandoc.utils.stringify(first_block)
  local rule, body = match_rule(raw_text)
  if not rule then
    return pandoc.Div(el.content, pandoc.Attr("", { "admonition" }, {}))
  end

  local blocks = {}
  for i, block in ipairs(el.content) do
    blocks[i] = block
  end

  local first_inlines = {
    pandoc.Span({ pandoc.Str(rule.label) }, pandoc.Attr("", { "admonition-label" }, {}))
  }

  if body ~= "" then
    first_inlines[#first_inlines + 1] = pandoc.Space()
    local content_inlines = text_to_inlines(body)
    for _, inline in ipairs(content_inlines) do
      first_inlines[#first_inlines + 1] = inline
    end
  end

  blocks[1] = pandoc.Para(first_inlines)
  return pandoc.Div(blocks, pandoc.Attr("", { "admonition", rule.class }, {}))
end
