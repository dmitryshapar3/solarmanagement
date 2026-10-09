export function validPriceFeedUrl(value: string): boolean {
  try { const url = new URL(value); return value.length <= 2048 && !/[\u0000-\u001f\u007f]/.test(value) && url.protocol === "https:" && (!url.port || url.port === "443") && !url.username && !url.password && !url.hash && url.hostname.includes(".") && !/^[\d.]+$/.test(url.hostname) && !url.hostname.startsWith("[") && !/\.(local|localhost)$/i.test(url.hostname); } catch { return false; }
}
