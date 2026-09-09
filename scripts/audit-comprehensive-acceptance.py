#!/usr/bin/env python3
"""Audit the runtime-only comprehensive MD2Word acceptance artifact.

The script reads synthetic DOCX/PDF artifacts from output/. It never writes
document contents outside the explicitly requested report paths.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import zipfile
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable
from xml.etree import ElementTree as ET


W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PR = "http://schemas.openxmlformats.org/package/2006/relationships"
WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
NS = {"w": W, "r": R, "pr": PR, "wp": WP}
W_TAG = f"{{{W}}}"
REL_EXTERNAL = "External"
EMU_PER_TWIP = 635
EMU_PER_POINT = 12_700


@dataclass
class Finding:
    check: str
    passed: bool
    detail: str
    evidence: Any | None = None

    def as_dict(self) -> dict[str, Any]:
        value: dict[str, Any] = {
            "check": self.check,
            "passed": self.passed,
            "detail": self.detail,
        }
        if self.evidence is not None:
            value["evidence"] = self.evidence
        return value


def qn(local: str) -> str:
    return f"{W_TAG}{local}"


def attr(element: ET.Element | None, local: str) -> str | None:
    if element is None:
        return None
    return element.get(qn(local))


def text_of(element: ET.Element) -> str:
    return "".join(node.text or "" for node in element.iter(qn("t")))


def paragraph_lines(paragraph: ET.Element) -> list[str]:
    lines = [""]
    for node in paragraph.iter():
        if node.tag == qn("t"):
            lines[-1] += node.text or ""
        elif node.tag == qn("tab"):
            lines[-1] += "\t"
        elif node.tag in {qn("br"), qn("cr")}:
            lines.append("")
    return lines


def paragraph_style_id(paragraph: ET.Element) -> str | None:
    return attr(paragraph.find("./w:pPr/w:pStyle", NS), "val")


def top_level_body_child(element: ET.Element, parent_map: dict[ET.Element, ET.Element]) -> ET.Element:
    current = element
    while parent_map.get(current) is not None and parent_map[current].tag != qn("body"):
        current = parent_map[current]
    return current


def paragraph_is_structurally_empty(paragraph: ET.Element) -> bool:
    if text_of(paragraph).strip():
        return False
    blocked = {
        qn("drawing"),
        qn("pict"),
        qn("object"),
        qn("fldSimple"),
        qn("instrText"),
        qn("br"),
        qn("lastRenderedPageBreak"),
        qn("bookmarkStart"),
        qn("bookmarkEnd"),
    }
    return not any(node.tag in blocked for node in paragraph.iter())


def element_label(element: ET.Element) -> str:
    if element.tag == qn("tbl"):
        value = text_of(element).strip().replace("\n", " ")
        return f"table:{value[:60]}"
    if element.tag == qn("p"):
        value = text_of(element).strip().replace("\n", " ")
        return f"paragraph:{value[:60]}"
    return element.tag.rsplit("}", 1)[-1]


def find_top_level_index(children: list[ET.Element], marker: str) -> int:
    for index, child in enumerate(children):
        if marker in text_of(child):
            return index
    raise ValueError(f"Top-level marker was not found: {marker}")


def safe_number(value: str | None, default: int = 0) -> int:
    if value is None:
        return default
    try:
        return int(value)
    except ValueError:
        return default


def style_catalog(styles_root: ET.Element) -> tuple[dict[str, ET.Element], dict[str, str]]:
    by_id: dict[str, ET.Element] = {}
    names: dict[str, str] = {}
    for style in styles_root.findall("./w:style", NS):
        style_id = attr(style, "styleId")
        if not style_id:
            continue
        by_id[style_id] = style
        names[style_id] = attr(style.find("./w:name", NS), "val") or style_id
    return by_id, names


def relationship_records(archive: zipfile.ZipFile) -> list[dict[str, str]]:
    result: list[dict[str, str]] = []
    for name in archive.namelist():
        if not name.endswith(".rels"):
            continue
        root = ET.fromstring(archive.read(name))
        for relationship in root.findall("./pr:Relationship", NS):
            result.append(
                {
                    "part": name,
                    "id": relationship.get("Id", ""),
                    "type": relationship.get("Type", ""),
                    "target": relationship.get("Target", ""),
                    "targetMode": relationship.get("TargetMode", ""),
                }
            )
    return result


def visible_parts(archive: zipfile.ZipFile) -> dict[str, ET.Element]:
    result: dict[str, ET.Element] = {}
    pattern = re.compile(
        r"^word/(document|header\d+|footer\d+|footnotes|endnotes|comments)\.xml$",
        re.IGNORECASE,
    )
    for name in archive.namelist():
        if pattern.match(name):
            result[name] = ET.fromstring(archive.read(name))
    return result


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def audit_docx(docx_path: Path, template_path: Path, expectations: dict[str, Any]) -> tuple[list[Finding], dict[str, Any]]:
    findings: list[Finding] = []
    metrics: dict[str, Any] = {}

    with zipfile.ZipFile(docx_path) as archive:
        document_root = ET.fromstring(archive.read("word/document.xml"))
        styles_root = ET.fromstring(archive.read("word/styles.xml"))
        visible = visible_parts(archive)
        relationships = relationship_records(archive)
        names = set(archive.namelist())

        body = document_root.find("./w:body", NS)
        if body is None:
            raise ValueError("word/document.xml has no body")
        children = list(body)
        parent_map = {child: parent for parent in document_root.iter() for child in parent}

        bookmark_starts = document_root.findall(".//w:bookmarkStart", NS)
        bookmark_names = [attr(node, "name") or "" for node in bookmark_starts]
        bookmark_counts = Counter(bookmark_names)
        expected_bookmarks = expectations["expectedBookmarks"]
        bookmark_failures = {
            name: bookmark_counts[name] for name in expected_bookmarks if bookmark_counts[name] != 1
        }
        findings.append(
            Finding(
                "bookmarks.unique",
                not bookmark_failures,
                "Required bookmarks are unique." if not bookmark_failures else "Required bookmark counts differ from one.",
                bookmark_failures or {name: bookmark_counts[name] for name in expected_bookmarks},
            )
        )

        start_node = next(
            (node for node in bookmark_starts if attr(node, "name") == "MANUAL_BODY_START"),
            None,
        )
        end_node = next(
            (node for node in bookmark_starts if attr(node, "name") == "MANUAL_BODY_END"),
            None,
        )
        if start_node is None or end_node is None:
            raise ValueError("Body bookmarks are missing")
        start_child = top_level_body_child(start_node, parent_map)
        end_child = top_level_body_child(end_node, parent_map)
        start_index = children.index(start_child)
        end_index = children.index(end_child)
        body_children = children[start_index + 1 : end_index]
        findings.append(
            Finding(
                "bookmarks.order",
                start_index < end_index,
                f"Body bookmark hosts are ordered ({start_index} < {end_index}).",
            )
        )

        style_by_id, style_names = style_catalog(styles_root)
        referenced_style_ids: list[str] = []
        for root in visible.values():
            referenced_style_ids.extend(
                value
                for node in root.findall(".//w:pStyle", NS) + root.findall(".//w:rStyle", NS)
                if (value := attr(node, "val"))
            )
        missing_styles = sorted(set(referenced_style_ids) - set(style_by_id))
        findings.append(
            Finding(
                "styles.references-resolve",
                not missing_styles,
                "Every paragraph/character style reference resolves in the final styles part."
                if not missing_styles
                else "Some final style references are dangling.",
                missing_styles,
            )
        )

        forbidden_style_fragments = [
            value.casefold() for value in expectations["forbiddenReferencedStyleFragments"]
        ]
        forbidden_style_refs = []
        for style_id in sorted(set(referenced_style_ids)):
            identity = f"{style_id} {style_names.get(style_id, '')}".casefold()
            if any(fragment in identity for fragment in forbidden_style_fragments):
                forbidden_style_refs.append(
                    {"styleId": style_id, "styleName": style_names.get(style_id, "")}
                )
        findings.append(
            Finding(
                "styles.no-html-or-temporary-references",
                not forbidden_style_refs,
                "No visible part references HTML/manual/marker styles."
                if not forbidden_style_refs
                else "Visible content still references imported or temporary styles.",
                forbidden_style_refs,
            )
        )

        temporary_definitions = []
        for style_id, style_name in style_names.items():
            identity = f"{style_id} {style_name}".casefold()
            if any(fragment in identity for fragment in ("manual-", "md2word-role-marker")):
                temporary_definitions.append({"styleId": style_id, "styleName": style_name})
        findings.append(
            Finding(
                "styles.no-temporary-definitions",
                not temporary_definitions,
                "No manual/role-marker style definitions remain."
                if not temporary_definitions
                else "Temporary style definitions remain.",
                temporary_definitions,
            )
        )

        visible_text_by_part = {name: text_of(root) for name, root in visible.items()}
        visible_text = "\n".join(visible_text_by_part.values())
        required_counts = {
            marker: visible_text.count(marker)
            for marker in expectations["requiredVisibleExactlyOnce"]
        }
        wrong_required_counts = {
            marker: count for marker, count in required_counts.items() if count != 1
        }
        findings.append(
            Finding(
                "text.required-sentinels",
                not wrong_required_counts,
                "Every required synthetic sentinel appears exactly once."
                if not wrong_required_counts
                else "Required sentinel counts are incorrect.",
                wrong_required_counts or required_counts,
            )
        )

        forbidden_hits = {
            fragment: visible_text.casefold().count(fragment.casefold())
            for fragment in expectations["forbiddenVisibleFragments"]
            if fragment.casefold() in visible_text.casefold()
        }
        findings.append(
            Finding(
                "text.no-html-or-role-leakage",
                not forbidden_hits,
                "No forbidden visible HTML/role/placeholder fragments were found."
                if not forbidden_hits
                else "Forbidden visible fragments leaked into the final package.",
                forbidden_hits,
            )
        )

        invalid_caption_count = visible_text.count("MERMAID-INVALID-CAPTION")
        invalid_source_count = visible_text.count("this is intentionally invalid mermaid syntax")
        findings.append(
            Finding(
                "mermaid.auto-fallback",
                invalid_caption_count == 0 and invalid_source_count == 1,
                "The failed Mermaid source remains code and its caption is not orphaned."
                if invalid_caption_count == 0 and invalid_source_count == 1
                else "The failed Mermaid fallback text/caption differs from the contract.",
                {
                    "invalidSourceVisibleCount": invalid_source_count,
                    "orphanCaptionVisibleCount": invalid_caption_count,
                },
            )
        )

        alt_chunks = [
            name
            for name in names
            if "altchunk" in name.casefold() or "afchunk" in name.casefold()
        ]
        alt_chunk_elements = document_root.findall(".//w:altChunk", NS)
        findings.append(
            Finding(
                "package.no-altchunk",
                not alt_chunks and not alt_chunk_elements,
                "The final DOCX has no alternative-format HTML import part."
                if not alt_chunks and not alt_chunk_elements
                else "Alternative-format import residue remains.",
                {"parts": alt_chunks, "elements": len(alt_chunk_elements)},
            )
        )

        floating_anchors = document_root.findall(".//wp:anchor", NS)
        text_boxes = document_root.findall(".//w:txbxContent", NS)
        legacy_pictures = document_root.findall(".//w:pict", NS)
        findings.append(
            Finding(
                "package.no-unexpected-floating-or-textbox-content",
                not floating_anchors and not text_boxes and not legacy_pictures,
                "The final DOCX contains only expected inline drawings; no floating anchor, text box, or legacy HTML picture residue remains."
                if not floating_anchors and not text_boxes and not legacy_pictures
                else "Unexpected floating, text-box, or legacy picture content remains.",
                {
                    "floatingAnchors": len(floating_anchors),
                    "textBoxes": len(text_boxes),
                    "legacyPictures": len(legacy_pictures),
                },
            )
        )

        external_relationships = [
            record
            for record in relationships
            if record["targetMode"].casefold() == REL_EXTERNAL.casefold()
        ]
        findings.append(
            Finding(
                "package.no-external-relationships",
                not external_relationships,
                "No external relationships remain."
                if not external_relationships
                else "External relationships remain in the final package.",
                external_relationships,
            )
        )

        imported_body_paragraphs = [
            element
            for element in body_children
            if element.tag == qn("p") and "[DOM-BODY-" in text_of(element)
        ]
        imported_body_geometry = []
        for paragraph in imported_body_paragraphs:
            ppr = paragraph.find("./w:pPr", NS)
            imported_body_geometry.append(
                {
                    "text": text_of(paragraph),
                    "styleId": paragraph_style_id(paragraph),
                    "styleName": style_names.get(paragraph_style_id(paragraph) or "", ""),
                    "directSpacing": ET.tostring(
                        ppr.find("./w:spacing", NS), encoding="unicode"
                    )
                    if ppr is not None and ppr.find("./w:spacing", NS) is not None
                    else None,
                    "directIndent": ET.tostring(
                        ppr.find("./w:ind", NS), encoding="unicode"
                    )
                    if ppr is not None and ppr.find("./w:ind", NS) is not None
                    else None,
                    "directAlignment": attr(
                        ppr.find("./w:jc", NS) if ppr is not None else None, "val"
                    ),
                }
            )
        geometry_failures = [
            row
            for row in imported_body_geometry
            if row["styleName"] != expectations["styles"]["body"]["name"]
            or row["directSpacing"] is not None
            or row["directIndent"] is not None
            or row["directAlignment"] is not None
        ]
        findings.append(
            Finding(
                "spacing.imported-body-style-driven",
                len(imported_body_paragraphs) == 2 and not geometry_failures,
                "Imported body controls use the target style without direct spacing/indent/alignment."
                if len(imported_body_paragraphs) == 2 and not geometry_failures
                else "Imported body controls contain unexpected geometry or style.",
                geometry_failures or imported_body_geometry,
            )
        )

        empty_records = []
        for index, child in enumerate(body_children):
            if child.tag != qn("p") or not paragraph_is_structurally_empty(child):
                continue
            global_index = start_index + 1 + index
            previous = children[global_index - 1] if global_index > 0 else None
            following = children[global_index + 1] if global_index + 1 < len(children) else None
            ppr = child.find("./w:pPr", NS)
            direct_children = []
            if ppr is not None:
                direct_children = [
                    node.tag.rsplit("}", 1)[-1]
                    for node in list(ppr)
                    if node.tag != qn("pStyle")
                ]
            empty_records.append(
                {
                    "bodyChildIndex": index,
                    "styleId": paragraph_style_id(child),
                    "styleName": style_names.get(paragraph_style_id(child) or "", ""),
                    "betweenTables": bool(
                        previous is not None
                        and following is not None
                        and previous.tag == qn("tbl")
                        and following.tag == qn("tbl")
                    ),
                    "runCount": len(child.findall("./w:r", NS)),
                    "hasVanish": child.find(".//w:vanish", NS) is not None,
                    "directPPrChildren": direct_children,
                    "previous": element_label(previous) if previous is not None else None,
                    "next": element_label(following) if following is not None else None,
                }
            )
        body_style_name = expectations["styles"]["body"]["name"]
        unexpected_empty = [
            row
            for row in empty_records
            if not row["betweenTables"]
            or row["styleName"] != body_style_name
            or row["runCount"] != 0
            or row["hasVanish"]
            or row["directPPrChildren"]
        ]
        expected_separator_count = expectations["expectedAdjacentTableSeparators"]
        findings.append(
            Finding(
                "spacing.no-unexpected-empty-top-level-paragraphs",
                len(empty_records) == expected_separator_count and not unexpected_empty,
                f"Only the {expected_separator_count} required table separators remain."
                if len(empty_records) == expected_separator_count and not unexpected_empty
                else "Unexpected or malformed empty top-level body paragraphs remain.",
                {"all": empty_records, "unexpected": unexpected_empty},
            )
        )

        document_paragraphs = document_root.findall(".//w:p", NS)
        all_tables = document_root.findall(".//w:tbl", NS)
        body_tables = [element for element in body_children if element.tag == qn("tbl")]
        drawings = document_root.findall(".//w:drawing", NS)
        metrics.update(
            {
                "paragraphs": len(document_paragraphs),
                "tables": len(all_tables),
                "bodyTopLevelTables": len(body_tables),
                "drawings": len(drawings),
                "emptyTopLevelBodyParagraphs": len(empty_records),
                "styleDefinitions": len(style_by_id),
                "styleReferences": len(referenced_style_ids),
            }
        )
        minimums = expectations["expectedMinimums"]
        count_failures = {
            key: {"actual": metrics[key], "minimum": minimums[key]}
            for key in ("paragraphs", "tables")
            if metrics[key] < minimums[key]
        }
        if metrics["drawings"] < minimums["images"]:
            count_failures["images"] = {
                "actual": metrics["drawings"],
                "minimum": minimums["images"],
            }
        findings.append(
            Finding(
                "structure.minimum-complexity",
                not count_failures,
                "The generated document meets the minimum complexity matrix."
                if not count_failures
                else "The generated document is missing expected complex structures.",
                count_failures or metrics,
            )
        )

        long_table = next(
            (table for table in document_root.findall(".//w:tbl", NS) if "[LONG-01]" in text_of(table)),
            None,
        )
        long_rows = long_table.findall("./w:tr", NS) if long_table is not None else []
        long_header = (
            long_rows[0].find("./w:trPr/w:tblHeader", NS) is not None if long_rows else False
        )
        findings.append(
            Finding(
                "tables.long-table-header",
                len(long_rows) >= minimums["longTableRows"] and long_header,
                "The long table keeps all rows and a repeat-table-header marker."
                if len(long_rows) >= minimums["longTableRows"] and long_header
                else "The long table row count or repeat header is incorrect.",
                {"rows": len(long_rows), "headerMarked": long_header},
            )
        )

        merged_table = next(
            (table for table in document_root.findall(".//w:tbl", NS) if "[MERGE-V]" in text_of(table)),
            None,
        )
        merge_metrics = {
            "vMerge": len(merged_table.findall(".//w:vMerge", NS)) if merged_table is not None else 0,
            "gridSpan": len(merged_table.findall(".//w:gridSpan", NS)) if merged_table is not None else 0,
        }
        findings.append(
            Finding(
                "tables.regular-merged-structure",
                merge_metrics["vMerge"] >= 2 and merge_metrics["gridSpan"] >= 1,
                "The regular HTML table preserves vertical and horizontal merges."
                if merge_metrics["vMerge"] >= 2 and merge_metrics["gridSpan"] >= 1
                else "The regular merged-table structure was lost.",
                merge_metrics,
            )
        )

        body_table_spacing = [
            text_of(table)[:80]
            for table in body_tables
            if table.find("./w:tblPr/w:tblCellSpacing", NS) is not None
            or table.find(".//w:trPr/w:tblCellSpacing", NS) is not None
        ]
        findings.append(
            Finding(
                "tables.no-cell-spacing",
                not body_table_spacing,
                "Body tables contain no table/row cell-spacing residue."
                if not body_table_spacing
                else "Some body tables retain HTML cell spacing.",
                body_table_spacing,
            )
        )

        code_paragraph = next(
            (
                paragraph
                for paragraph in document_root.findall(".//w:p", NS)
                if "[CODE-L1]" in text_of(paragraph)
            ),
            None,
        )
        actual_code_lines = paragraph_lines(code_paragraph) if code_paragraph is not None else []
        normalized_code_lines = [
            line.replace("\u00a0", " ") for line in actual_code_lines
        ]
        expected_code_lines = expectations["expectedCodeLines"]
        findings.append(
            Finding(
                "code.whitespace-and-breaks",
                normalized_code_lines == expected_code_lines,
                "The fenced code block preserves lines, blank line, indentation, double spaces, and tab expansion; Word's non-breaking-space encoding is visually equivalent."
                if normalized_code_lines == expected_code_lines
                else "The fenced code block differs from the source whitespace contract.",
                {
                    "actual": actual_code_lines,
                    "normalized": normalized_code_lines,
                    "expected": expected_code_lines,
                },
            )
        )

        internal_bookmarks = {
            name
            for name in bookmark_names
            if name.startswith("md2word_bm_")
        }
        internal_anchors = [
            attr(node, "anchor")
            for node in document_root.findall(".//w:hyperlink", NS)
            if (attr(node, "anchor") or "").startswith("md2word_bm_")
        ]
        missing_internal_targets = sorted(
            {anchor for anchor in internal_anchors if anchor and anchor not in internal_bookmarks}
        )
        findings.append(
            Finding(
                "links.internal-bookmarks",
                len(internal_anchors) == 2
                and len(internal_bookmarks) == 1
                and not missing_internal_targets,
                "Two internal links share one resolvable ASCII-safe bookmark."
                if len(internal_anchors) == 2
                and len(internal_bookmarks) == 1
                and not missing_internal_targets
                else "Internal link/bookmark counts or targets are incorrect.",
                {
                    "anchors": internal_anchors,
                    "bookmarks": sorted(internal_bookmarks),
                    "missingTargets": missing_internal_targets,
                },
            )
        )

        heading_num_ids = []
        for paragraph in document_root.findall(".//w:p", NS):
            style_id = paragraph_style_id(paragraph)
            style_name = style_names.get(style_id or "", "")
            if style_name.startswith("合成验收标题") and "[DOM-" in text_of(paragraph):
                num_id = attr(paragraph.find("./w:pPr/w:numPr/w:numId", NS), "val")
                if num_id:
                    heading_num_ids.append(num_id)
        findings.append(
            Finding(
                "numbering.headings-share-num-id",
                len(heading_num_ids) >= 6 and len(set(heading_num_ids)) == 1,
                "Generated headings share one native Word multilevel numbering instance."
                if len(heading_num_ids) >= 6 and len(set(heading_num_ids)) == 1
                else "Heading numbering is missing or split across instances.",
                heading_num_ids,
            )
        )

        list_marker_prefixes = (
            "[UL-",
            "[OL-",
            "[LOOSE-1]",
            "[LOOSE-2]",
            "[RESTART-",
        )
        list_records = []
        for paragraph in document_root.findall(".//w:p", NS):
            value = text_of(paragraph)
            if any(prefix in value for prefix in list_marker_prefixes):
                list_records.append(
                    {
                        "text": value,
                        "styleName": style_names.get(paragraph_style_id(paragraph) or "", ""),
                        "numId": attr(paragraph.find("./w:pPr/w:numPr/w:numId", NS), "val"),
                        "level": attr(paragraph.find("./w:pPr/w:numPr/w:ilvl", NS), "val"),
                    }
                )
        bad_lists = [row for row in list_records if row["numId"] in (None, "0")]
        findings.append(
            Finding(
                "numbering.lists-remain-native",
                len(list_records) >= 12 and not bad_lists,
                "Ordered/unordered list sentinels remain native numbered paragraphs."
                if len(list_records) >= 12 and not bad_lists
                else "Some list sentinels lost native numbering.",
                bad_lists or list_records,
            )
        )

        sect_pr = body.find("./w:sectPr", NS)
        page_size = sect_pr.find("./w:pgSz", NS) if sect_pr is not None else None
        page_margin = sect_pr.find("./w:pgMar", NS) if sect_pr is not None else None
        usable_twips = (
            safe_number(attr(page_size, "w"))
            - safe_number(attr(page_margin, "left"))
            - safe_number(attr(page_margin, "right"))
        )
        maximum_image_width = usable_twips * EMU_PER_TWIP
        extents = []
        for extent in document_root.findall(".//wp:extent", NS):
            cx = safe_number(extent.get("cx"))
            cy = safe_number(extent.get("cy"))
            extents.append({"cx": cx, "cy": cy})
        oversized = [
            extent
            for extent in extents
            if extent["cx"] > maximum_image_width + EMU_PER_POINT
        ]
        findings.append(
            Finding(
                "images.within-body-width",
                len(extents) >= minimums["images"] and not oversized,
                "All inline images fit the section body width within the 1pt layout tolerance."
                if len(extents) >= minimums["images"] and not oversized
                else "An image is missing or exceeds the available body width.",
                {
                    "availableWidthEmu": maximum_image_width,
                    "extents": extents,
                    "oversized": oversized,
                },
            )
        )

        caption_markers = (
            "SYNTHETIC-WIDE-CAPTION",
            "SYNTHETIC-SMALL-CAPTION",
            "MERMAID-CAPTION-OK",
            "MERMAID-CATION-COMPAT",
        )
        caption_adjacency = []
        for marker in caption_markers:
            caption_index = find_top_level_index(children, marker)
            previous = children[caption_index - 1] if caption_index > 0 else None
            caption = children[caption_index]
            caption_adjacency.append(
                {
                    "marker": marker,
                    "captionStyle": style_names.get(paragraph_style_id(caption) or "", ""),
                    "captionHasDrawing": caption.find(".//w:drawing", NS) is not None,
                    "previousIsImageParagraph": bool(
                        previous is not None
                        and previous.tag == qn("p")
                        and previous.find(".//w:drawing", NS) is not None
                    ),
                }
            )
        bad_caption_adjacency = [
            row
            for row in caption_adjacency
            if row["captionStyle"] != "合成验收图题"
            or row["captionHasDrawing"]
            or not row["previousIsImageParagraph"]
        ]
        findings.append(
            Finding(
                "images.captions-are-separate-and-adjacent",
                not bad_caption_adjacency,
                "Every successful figure has a separate adjacent target-style caption."
                if not bad_caption_adjacency
                else "A figure caption is not separate, adjacent, or correctly styled.",
                bad_caption_adjacency or caption_adjacency,
            )
        )

        fixed_text_expectations = (
            "合成复杂验收模板｜仅限本地测试",
            "固定键",
            "固定值",
            "复杂合成链路验收",
            "只含公开合成内容",
            "复杂合成验收初始记录",
        )
        fixed_missing = [value for value in fixed_text_expectations if value not in visible_text]
        findings.append(
            Finding(
                "template.fixed-and-metadata-content",
                not fixed_missing,
                "Header, fixed regions, cover metadata, and version metadata remain present."
                if not fixed_missing
                else "Expected template-fixed or metadata content is missing.",
                fixed_missing,
            )
        )

    with zipfile.ZipFile(template_path) as template_archive:
        template_document = ET.fromstring(template_archive.read("word/document.xml"))
        template_text = text_of(template_document)
        output_text = text_of(document_root)
        unchanged_fixed_sentinels = ("[FIXED-BEFORE]", "[CTRL-BODY-1]", "[CTRL-BODY-2]", "[FIXED-AFTER]")
        fixed_snapshot_failures = [
            marker
            for marker in unchanged_fixed_sentinels
            if template_text.count(marker) != 1 or output_text.count(marker) != 1
        ]
        findings.append(
            Finding(
                "template.fixed-snapshot",
                not fixed_snapshot_failures,
                "Template control/fixed sentinels remain exactly once before and after conversion."
                if not fixed_snapshot_failures
                else "A fixed-region sentinel changed or duplicated.",
                fixed_snapshot_failures,
            )
        )

    return findings, metrics


def pdf_lines(page: Any, tolerance: float = 2.0) -> list[dict[str, Any]]:
    words = page.extract_words(
        x_tolerance=1,
        y_tolerance=2,
        keep_blank_chars=False,
        use_text_flow=True,
    )
    rows: list[dict[str, Any]] = []
    for word in sorted(words, key=lambda item: (float(item["top"]), float(item["x0"]))):
        top = float(word["top"])
        row = next((candidate for candidate in rows if abs(candidate["top"] - top) <= tolerance), None)
        if row is None:
            row = {"top": top, "bottom": float(word["bottom"]), "words": []}
            rows.append(row)
        row["words"].append(word)
        row["bottom"] = max(row["bottom"], float(word["bottom"]))
    for row in rows:
        row["words"].sort(key=lambda item: float(item["x0"]))
        row["text"] = " ".join(str(item["text"]) for item in row["words"])
        row["x0"] = min(float(item["x0"]) for item in row["words"])
        row["x1"] = max(float(item["x1"]) for item in row["words"])
    return rows


def find_pdf_marker(lines_by_page: list[list[dict[str, Any]]], marker: str) -> dict[str, Any] | None:
    normalized_marker = re.sub(r"\s+", "", marker)
    for page_index, lines in enumerate(lines_by_page):
        for line in lines:
            normalized_line = re.sub(r"\s+", "", line["text"])
            if normalized_marker in normalized_line:
                return {"page": page_index + 1, **line}
    return None


def audit_pdf(pdf_path: Path, expectations: dict[str, Any]) -> tuple[list[Finding], dict[str, Any]]:
    import pdfplumber  # type: ignore

    findings: list[Finding] = []
    metrics: dict[str, Any] = {}
    with pdfplumber.open(pdf_path) as pdf:
        page_count = len(pdf.pages)
        metrics["pages"] = page_count
        minimum_pages = expectations["expectedMinimums"]["pages"]
        findings.append(
            Finding(
                "pdf.minimum-pages",
                page_count >= minimum_pages,
                f"Word PDF contains {page_count} pages (minimum {minimum_pages}).",
            )
        )

        lines_by_page = [pdf_lines(page) for page in pdf.pages]
        page_text = [page.extract_text(x_tolerance=1, y_tolerance=2) or "" for page in pdf.pages]
        all_text = "\n".join(page_text)
        pdf_required = (
            "[CTRL-BODY-1]",
            "[CTRL-BODY-2]",
            "[DOM-BODY-1]",
            "[DOM-BODY-2]",
            "[DOM-END-BODY]",
            "[LONG-56]",
            "MERMAID-CAPTION-OK",
        )
        missing_pdf_markers = [
            marker
            for marker in pdf_required
            if re.sub(r"\s+", "", marker) not in re.sub(r"\s+", "", all_text)
        ]
        findings.append(
            Finding(
                "pdf.required-sentinels",
                not missing_pdf_markers,
                "Key structure and spacing sentinels are extractable from the Word PDF."
                if not missing_pdf_markers
                else "Some key sentinels are missing from the PDF text layer.",
                missing_pdf_markers,
            )
        )

        clipping = []
        for page_index, page in enumerate(pdf.pages):
            for word in page.extract_words(x_tolerance=1, y_tolerance=2):
                x0 = float(word["x0"])
                x1 = float(word["x1"])
                top = float(word["top"])
                bottom = float(word["bottom"])
                if x0 < -0.5 or x1 > float(page.width) + 0.5 or top < -0.5 or bottom > float(page.height) + 0.5:
                    clipping.append(
                        {
                            "page": page_index + 1,
                            "text": word["text"],
                            "x0": x0,
                            "x1": x1,
                            "top": top,
                            "bottom": bottom,
                            "pageWidth": float(page.width),
                            "pageHeight": float(page.height),
                        }
                    )
        findings.append(
            Finding(
                "pdf.text-within-page-bounds",
                not clipping,
                "No PDF text boxes extend beyond page bounds."
                if not clipping
                else "Some PDF text boxes extend beyond page bounds.",
                clipping[:20],
            )
        )

        spacing_results = []
        spacing_failures = []
        for pair in expectations["spacingControlPairs"]:
            control_first = find_pdf_marker(lines_by_page, pair["control"][0])
            control_second = find_pdf_marker(lines_by_page, pair["control"][1])
            imported_first = find_pdf_marker(lines_by_page, pair["imported"][0])
            imported_second = find_pdf_marker(lines_by_page, pair["imported"][1])
            row: dict[str, Any] = {
                "name": pair["name"],
                "control": [control_first, control_second],
                "imported": [imported_first, imported_second],
            }
            if (
                control_first is None
                or control_second is None
                or imported_first is None
                or imported_second is None
                or control_first["page"] != control_second["page"]
                or imported_first["page"] != imported_second["page"]
            ):
                row["passed"] = False
                row["reason"] = "A pair is missing or crosses a page boundary."
            else:
                control_delta = float(control_second["top"]) - float(control_first["top"])
                imported_delta = float(imported_second["top"]) - float(imported_first["top"])
                difference = abs(imported_delta - control_delta)
                row.update(
                    {
                        "controlDeltaPt": round(control_delta, 3),
                        "importedDeltaPt": round(imported_delta, 3),
                        "differencePt": round(difference, 3),
                        "tolerancePt": pair["maxDeltaDifferencePt"],
                        "passed": difference <= float(pair["maxDeltaDifferencePt"]),
                    }
                )
            spacing_results.append(row)
            if not row["passed"]:
                spacing_failures.append(row)
        findings.append(
            Finding(
                "pdf.template-vs-dom-spacing",
                not spacing_failures,
                "Template-native and DOM-imported same-style paragraph gaps match within tolerance."
                if not spacing_failures
                else "DOM-imported paragraph gaps differ from the template-native control.",
                spacing_results,
            )
        )

        header_pages = [
            index + 1
            for index, value in enumerate(page_text)
            if "合成标识" in value and "说明" in value
        ]
        long_table_pages = [
            index + 1
            for index, value in enumerate(page_text)
            if "[LONG-" in value
        ]
        repeated_header_ok = bool(long_table_pages) and all(
            page in header_pages for page in long_table_pages
        )
        findings.append(
            Finding(
                "pdf.long-table-repeat-header",
                repeated_header_ok and len(long_table_pages) >= 2,
                "Every PDF page containing long-table rows also contains the repeated header."
                if repeated_header_ok and len(long_table_pages) >= 2
                else "The long table did not span pages with a visible repeated header.",
                {
                    "longTablePages": long_table_pages,
                    "headerPages": header_pages,
                },
            )
        )

    return findings, metrics


def make_markdown_report(
    findings: list[Finding],
    metrics: dict[str, Any],
    docx_path: Path,
    pdf_path: Path | None,
) -> str:
    passed = sum(1 for finding in findings if finding.passed)
    failed = len(findings) - passed
    lines = [
        "# 复杂合成文档验收报告",
        "",
        f"- DOCX：`{docx_path.name}`",
        f"- PDF：`{pdf_path.name if pdf_path else '尚未提供'}`",
        f"- 结果：{passed} 项通过，{failed} 项失败",
        f"- 结论：{'通过' if failed == 0 else '未通过'}",
        "",
        "## 指标",
        "",
        "```json",
        json.dumps(metrics, ensure_ascii=False, indent=2),
        "```",
        "",
        "## 检查项",
        "",
    ]
    for finding in findings:
        icon = "PASS" if finding.passed else "FAIL"
        lines.append(f"- [{icon}] `{finding.check}`：{finding.detail}")
        if not finding.passed and finding.evidence is not None:
            lines.extend(
                [
                    "",
                    "  ```json",
                    *[
                        f"  {line}"
                        for line in json.dumps(
                            finding.evidence, ensure_ascii=False, indent=2
                        ).splitlines()
                    ],
                    "  ```",
                    "",
                ]
            )
    return "\n".join(lines).rstrip() + "\n"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--docx", type=Path, required=True)
    parser.add_argument("--template", type=Path, required=True)
    parser.add_argument("--expectations", type=Path, required=True)
    parser.add_argument("--pdf", type=Path)
    parser.add_argument("--out-json", type=Path, required=True)
    parser.add_argument("--out-md", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    expectations = load_json(args.expectations)
    findings, metrics = audit_docx(args.docx, args.template, expectations)
    if args.pdf is not None:
        pdf_findings, pdf_metrics = audit_pdf(args.pdf, expectations)
        findings.extend(pdf_findings)
        metrics.update(pdf_metrics)

    payload = {
        "schemaVersion": 1,
        "docx": str(args.docx.resolve()),
        "template": str(args.template.resolve()),
        "pdf": str(args.pdf.resolve()) if args.pdf else None,
        "summary": {
            "passed": sum(1 for finding in findings if finding.passed),
            "failed": sum(1 for finding in findings if not finding.passed),
            "overall": "passed" if all(finding.passed for finding in findings) else "failed",
        },
        "metrics": metrics,
        "findings": [finding.as_dict() for finding in findings],
    }
    args.out_json.parent.mkdir(parents=True, exist_ok=True)
    args.out_json.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    args.out_md.parent.mkdir(parents=True, exist_ok=True)
    args.out_md.write_text(
        make_markdown_report(findings, metrics, args.docx, args.pdf),
        encoding="utf-8",
    )

    print(json.dumps(payload["summary"], ensure_ascii=False))
    for finding in findings:
        if not finding.passed:
            print(f"FAIL {finding.check}: {finding.detail}", file=sys.stderr)
    return 0 if payload["summary"]["overall"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
