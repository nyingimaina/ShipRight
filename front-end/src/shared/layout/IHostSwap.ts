/** Host-metal swap as seen by the app; the header only displays safe byte counts and percentages. */
export interface IHostSwap {
  swapUsedBytes: number;
  swapTotalBytes: number;
  swapUsedPercent: number;
  available: boolean;
  configured: boolean;
}
