using ClosedXML.Excel;

namespace SerialDataLogger
{
    /// <summary>
    /// 조회 결과를 xlsx로 저장한다.
    /// </summary>
    public static class ExcelExporter
    {
        public static void Export(IReadOnlyList<Reading> readings, string path)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("측정데이터");

            string[] headers = { "시각", "채널", "값", "단위", "원문" };
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];

            var head = ws.Range(1, 1, 1, headers.Length);
            head.Style.Font.Bold = true;
            head.Style.Fill.BackgroundColor = XLColor.LightGray;

            int row = 2;
            foreach (var r in readings)
            {
                bool isError = r.Channel == "(오류)";

                // 문자열이 아니라 DateTime/double 그대로 넣는다.
                // 엑셀에서 정렬·필터·차트가 바로 되게 하려면 타입이 살아 있어야 한다.
                ws.Cell(row, 1).Value = r.Timestamp;
                ws.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";

                ws.Cell(row, 2).Value = r.Channel;

                if (isError)
                    ws.Cell(row, 3).Value = "";
                else
                    ws.Cell(row, 3).Value = r.Value;

                ws.Cell(row, 4).Value = r.Unit;

                // 원문은 항상 텍스트로 강제.
                // 안 그러면 엑셀이 "23.5"를 숫자로, 앞자리 0을 지운 값으로 바꿔버린다.
                ws.Cell(row, 5).SetValue(r.Raw);
                ws.Cell(row, 5).Style.NumberFormat.Format = "@";

                if (isError)
                    ws.Range(row, 1, row, headers.Length)
                      .Style.Fill.BackgroundColor = XLColor.MistyRose;

                row++;
            }

            // 머리글 고정 + 자동 필터: 수천 행을 스크롤할 때 열 이름이 안 보이면 못 쓴다.
            ws.SheetView.FreezeRows(1);
            ws.Range(1, 1, Math.Max(row - 1, 1), headers.Length).SetAutoFilter();
            ws.Columns().AdjustToContents();

            wb.SaveAs(path);
        }
    }
}
