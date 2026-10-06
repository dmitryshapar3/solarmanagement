export function exportRangeError(from: string, through: string, today: string): string | null {
  const valid = (date: string) => /^\d{4}-\d{2}-\d{2}$/.test(date) && Number.isFinite(Date.parse(`${date}T00:00:00Z`)) && new Date(`${date}T00:00:00Z`).toISOString().slice(0, 10) === date;
  if (!valid(from) || !valid(through)) return "Enter dates in YYYY-MM-DD format.";
  if (through < from) return "End date must be on or after the start date.";
  if (through > today) return "Choose today or an earlier date.";
  if ((Date.parse(`${through}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86400000 + 1 > 366) return "Choose a range of at most 366 days.";
  return null;
}
