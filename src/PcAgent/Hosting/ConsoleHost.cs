using PcAgent.Interop;

namespace PcAgent.Hosting;

/// <summary>대화형 실행(더블클릭·시작 메뉴·UAC 새 창)을 위한 콘솔 도우미. 선택은 숫자 키로 받아 한/영 입력 상태와 무관합니다.</summary>
internal static unsafe class ConsoleHost
{
    /// <summary>
    /// 이 프로세스만 붙어 있는 콘솔이면 true — 탐색기 더블클릭·시작 메뉴로 새 창이 열린 경우.
    /// cmd/PowerShell 안에서 실행하면 셸도 같은 콘솔에 붙어 있으므로 false.
    /// </summary>
    public static bool OwnsConsole()
    {
        try
        {
            uint* list = stackalloc uint[2];
            return Native.GetConsoleProcessList(list, 2) == 1;
        }
        catch
        {
            return false;
        }
    }

    public static void TrySetTitle(string title)
    {
        try { Console.Title = title; } catch { /* 콘솔 없음 */ }
    }

    public static void Pause(string message = "아무 키나 누르면 창이 닫힙니다.")
    {
        Console.WriteLine();
        Console.Write(message);
        try
        {
            if (Console.IsInputRedirected) Console.ReadLine();
            else Console.ReadKey(intercept: true);
        }
        catch
        {
            // 입력 불가(콘솔 없음) — 그냥 진행
        }
        Console.WriteLine();
    }

    /// <summary>
    /// 키 하나를 읽습니다(Enter 불필요). Esc 는 '0'(닫기/취소), 입력 끝(EOF)은 '\0'.
    /// 입력이 리디렉션된 경우(자동 테스트)에는 한 줄을 읽어 첫 글자를 씁니다.
    /// </summary>
    public static char ReadChoice()
    {
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine();
            if (line is null) return '\0';
            line = line.Trim().TrimStart('﻿');
            Console.WriteLine(line);
            return line.Length == 0 ? ' ' : char.ToUpperInvariant(line[0]);
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return '0';
            }
            if (char.IsControl(key.KeyChar) || key.KeyChar == '\0') continue;   // Enter·방향키 등 무시
            Console.WriteLine(key.KeyChar);
            return char.ToUpperInvariant(key.KeyChar);
        }
    }

    /// <summary>번호 목록 중 하나를 고를 때까지 묻습니다. EOF 면 '0'.</summary>
    public static char Choose(string prompt, string allowed)
    {
        while (true)
        {
            Console.Write(prompt);
            char c = ReadChoice();
            if (c == '\0') return '0';
            if (allowed.Contains(c)) return c;
            Console.WriteLine($"  {string.Join(", ", allowed.ToCharArray())} 중에서 눌러 주세요.");
        }
    }
}
