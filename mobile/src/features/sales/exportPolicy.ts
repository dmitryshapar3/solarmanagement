const valid = (date: string) => /^\d{4}-\d{2}-\d{2}$/.test(date) && Number.isFinite(Date.parse(`${date}T00:00:00Z`)) && new Date(`${date}T00:00:00Z`).toISOString().slice(0, 10) === date;

export function measuredSalesRecheckRange(from: string, through: string, today: string): { from: string; through: string } | null {
  if (!valid(today) || from < "2000-01-01" || from > today || exportRangeError(from, through, through)) return null;
  return { from, through: through < today ? through : today };
}

export function exportRangeError(from: string, through: string, today: string): string | null {
  if (!valid(from) || !valid(through)) return "Enter dates in YYYY-MM-DD format.";
  if (through < from) return "End date must be on or after the start date.";
  if (through > today) return "Choose today or an earlier date.";
  if ((Date.parse(`${through}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86400000 + 1 > 366) return "Choose a range of at most 366 days.";
  return null;
}
