import rawCapabilityManifest from "../../resources/conversion/capabilities.json";
import type { CapabilityManifest } from "../types";

/**
 * Browser mock seed from the same manifest that the packaged Worker reads.
 * Electron never trusts this bundled Renderer copy; it queries the Worker
 * through Main so the UI describes the exact installed conversion runtime.
 */
export const bundledCapabilityManifest = rawCapabilityManifest as unknown as CapabilityManifest;
