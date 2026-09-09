import { fireEvent, render, screen } from "@testing-library/react";
import { bundledCapabilityManifest } from "../data/capabilityManifest";
import { CapabilitiesPage } from "./CapabilitiesPage";

describe("CapabilitiesPage", () => {
  it("shows the versioned catalog and searchable Front Matter metadata", () => {
    render(
      <CapabilitiesPage
        manifest={bundledCapabilityManifest}
        loading={false}
        error={null}
        focusId={null}
        onRetry={() => undefined}
      />,
    );

    expect(screen.getByRole("heading", { name: "MD2Word 支持能力说明" })).toBeInTheDocument();
    expect(screen.getByText("v0.5.0")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: /Front Matter/ }));
    fireEvent.change(screen.getByLabelText("搜索能力说明"), { target: { value: "word_heading_numbering" } });

    expect(screen.getByText("word_heading_numbering")).toBeInTheDocument();
    expect(screen.getByText(/分别配置四级 Word 编号/)).toBeInTheDocument();
    expect(screen.queryByText("figure_captions")).not.toBeInTheDocument();
  });

  it("opens a requested capability on the matching section", () => {
    render(
      <CapabilitiesPage
        manifest={bundledCapabilityManifest}
        loading={false}
        error={null}
        focusId="CAP-METADATA-REPEAT-HEADERS"
        onRetry={() => undefined}
      />,
    );

    expect(screen.getByRole("tab", { name: /Front Matter/ })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText("word_repeat_table_headers")).toBeInTheDocument();
  });
});
