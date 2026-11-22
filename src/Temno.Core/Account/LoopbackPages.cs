namespace Temno.Core.Account;

/// <summary>
/// The pages the loopback listener answers with: frame 05 ("You’re signed in") and frame R2 ("Sign-in didn’t finish")
/// of the sign-in page design (WP-353, docs/verification/wp-353/SPEC.md). Each is self-contained: inline CSS and SVG,
/// no script, no web font and no request, so it renders under the listener's <c>default-src 'none'</c> policy with the
/// network down. The text uses Inter when it is installed and the system sans otherwise.
/// </summary>
internal static class LoopbackPages
{
    public const string Success = Head + "<title>You’re signed in</title>" + Shell + SuccessBody + Tail;

    public const string Failure = Head + "<title>Sign-in didn’t finish</title>" + Shell + FailureBody + Tail;

    private const string Head = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="color-scheme" content="dark light">
        """;

    private const string Shell = """
        <style>
        :root {
          color-scheme: dark light;
          --bg-page: #0B0B0D;
          --text-primary: #FFFFFF;
          --text-secondary: #AFB2BE;
          --text-tertiary: #969BA8;
          --accent-beam: #5160FE;
          --accent-ink: #9097FF;
          --status-success: #4FB85F;
          --status-warning: #F2C94C;
          --glow-beam: rgb(81 96 254 / 60%);
          --bloom: rgb(81 96 254 / .22);
          --hairline: rgb(81 96 254 / .55);
          --wm-w: 18rem;
          --wm-h: calc(var(--wm-w) * 220 / 913);
          --beam-y: calc(var(--wm-w) * 108.4 / 913);
          --stack-top: max(48px, calc(50vh - 204px));
          --bloom-w: max(560px, min(1100px, 55vw));
        }
        @supports (height: 1svh) { :root { --stack-top: max(48px, calc(50svh - 204px)); } }
        @media (prefers-color-scheme: light) {
          :root {
            --bg-page: #F2EDE4;
            --text-primary: #0B0B0D;
            --text-secondary: #4A4D5A;
            --text-tertiary: #5E6272;
            --accent-ink: #3A44C8;
            --status-success: #1B7A3C;
            --status-warning: #8A5A00;
            --glow-beam: rgb(81 96 254 / 35%);
            --bloom: rgb(255 255 255 / .85);
            --hairline: rgb(81 96 254 / .40);
          }
        }
        @media (max-width: 599px) { :root { --wm-w: 15rem; } }
        html { font-size: calc(100% + clamp(0px, (100vw - 1874px) * 0.002332, 1.6px)); background: var(--bg-page); -webkit-text-size-adjust: 100%; text-size-adjust: 100%; }
        body {
          position: relative; box-sizing: border-box; display: flex; flex-direction: column;
          min-height: 100vh; min-height: 100svh; margin: 0;
          padding-inline: clamp(0px, calc((100% - 300px) / 2), 20px); overflow-x: clip;
          background: var(--bg-page); color: var(--text-secondary);
          font-family: Inter, "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
          font-size: .875rem; line-height: 1.3125rem; -webkit-font-smoothing: antialiased;
        }
        .ground { position: absolute; inset: 0; overflow: hidden; pointer-events: none; }
        .bloom { position: absolute; left: 50%; top: calc(var(--stack-top) + var(--wm-h) / 2); width: var(--bloom-w); height: calc(var(--bloom-w) * .46); transform: translate(-50%, -50%); background: radial-gradient(closest-side, var(--bloom), transparent); }
        .hairline { position: absolute; left: 0; right: 0; top: calc(var(--stack-top) + var(--beam-y) - .5px); height: 1px; background: linear-gradient(90deg, transparent, var(--hairline) 50%, transparent); }
        main { position: relative; box-sizing: border-box; display: flex; flex-direction: column; align-items: center; width: min(24rem, 100%); margin-inline: auto; padding-top: var(--stack-top); text-align: center; }
        .wordmark { position: relative; display: block; width: var(--wm-w); height: var(--wm-h); border-radius: 2px; }
        .wordmark svg { position: relative; display: block; width: 100%; height: 100%; overflow: visible; }
        .wm-l { fill: var(--text-primary); fill-rule: evenodd; }
        .wm-b { fill: var(--accent-beam); fill-rule: evenodd; }
        .halo { position: absolute; left: -1.963%; top: 40.74%; width: 18.62%; height: 17.07%; background: var(--glow-beam); filter: blur(4.5px); }
        .icon { display: block; width: 1.75rem; height: 1.75rem; margin-top: 2.5rem; fill: currentColor; }
        .ok { color: var(--status-success); }
        .warn { color: var(--status-warning); }
        h1 { margin: 1.25rem 0 0; color: var(--text-primary); font-size: 1.75rem; line-height: 2.25rem; font-weight: 600; }
        .lede { max-width: 20rem; margin: .625rem 0 0; }
        .fallback { max-width: 18.75rem; margin: 1.75rem 0 0; font-size: .75rem; line-height: 1.125rem; color: var(--text-tertiary); }
        footer { position: relative; margin-top: auto; padding: 1.75rem 0; font-size: .75rem; line-height: 1.125rem; color: var(--text-tertiary); text-align: center; }
        a:focus-visible { outline: 2px solid var(--accent-ink); outline-offset: 3px; }
        a:focus:not(:focus-visible) { outline: none; }
        </style>
        </head>
        <body>
        <div class="ground" aria-hidden="true"><div class="bloom"></div><div class="hairline"></div></div>
        <main>
          <a class="wordmark" href="https://temno.io" rel="noreferrer noopener" aria-label="Temno"><span class="halo" aria-hidden="true"></span><svg viewBox="-3 0 913 220" aria-hidden="true" focusable="false"><path class="wm-l" transform="matrix(0.866443826 0 0 0.866443826 -85.518170871 -40.538904408)" d="M 117.5 152.094 C 117.425 151.737 116.703 151.844 116.58 151.5 L 116.562 123.5 C 116.589 109.886 116.036 95.993 116.92 82.5 L 180.5 48.894 C 180.545 49.196 181.109 49.205 181.184 49.5 L 181.174 72.5 L 181.263 115.5 L 182.5 116.079 L 227.5 116.178 C 228.938 127.47 228.18 139.897 228.021 151.5 C 227.783 151.532 227.733 151.941 227.5 151.995 L 189.5 152.096 L 117.5 152.094 Z M 227.5 298.1 L 169.5 297.87 C 143.36 294.196 124.499 276.603 120.213 250.5 C 118.644 240.949 119.119 231.139 119.112 221.5 C 119.103 211.329 118.694 200.786 119.5 190.833 L 180.5 190.669 C 180.548 191.027 181.115 191.148 181.197 191.5 L 181.158 222.5 C 181.172 227.178 180.624 232.023 182.182 236.5 C 183.43 240.086 186.785 242.991 190.5 243.739 L 202.5 244.218 L 226.5 244.207 C 226.763 244.419 227.237 244.169 227.5 244.382 L 228.101 245.5 L 228.044 297.5 C 227.991 297.765 227.532 297.832 227.5 298.1 Z"/><path class="wm-l" d="M 156.5 192.555 C 156.032 192.536 155.565 192.518 155.097 192.5 C 143.577 174.945 145.376 157.673 145.593 137.5 C 145.703 127.227 144.804 116.698 146.272 106.5 C 149.717 82.572 170.305 65.347 193.5 62.216 L 229.5 61.801 C 246.762 61.766 265.425 60.02 280.134 70.5 L 245.5 105.153 L 207.5 105.375 C 205.089 105.799 203.407 107.146 201.491 108.5 C 198.204 113.037 198.13 116.481 198.5 121.939 C 207.196 122.779 216.632 122.706 225.5 122.382 C 225.767 122.755 226.034 123.127 226.302 123.5 L 156.5 192.555 Z M 163.851 202.5 L 163.029 201.5 L 163.501 200.5 L 288.5 77.868 C 288.833 77.98 289.167 78.092 289.5 78.204 C 304.09 93.919 304.775 108.939 304.723 129.5 L 304.513 152.5 C 293.822 153.748 282.392 153.063 271.5 153.029 L 220.5 152.944 C 214.762 157.886 208.774 163.639 203.895 169.5 C 205.589 171.033 207.072 172.636 209.5 173.002 L 303.5 173.03 C 303.817 173.52 304.134 174.01 304.451 174.5 C 303.941 187.644 301.865 202.176 290.5 210.674 C 285.664 214.29 279.556 216.999 273.5 217.625 L 239.5 217.738 L 212.5 217.743 C 203.786 217.743 194.973 218.103 186.5 215.771 C 177.805 213.378 170.574 208.4 163.851 202.5 Z M 404.5 217.304 L 404.109 118.5 C 403.878 116.328 403.134 114.205 401.742 112.5 C 397.243 106.986 388.768 108.56 382.5 108.485 L 381.077 109.5 L 381.04 179.5 C 381.012 192.109 381.599 205.028 380.646 217.5 L 328.398 217.5 L 328.049 127.5 L 328.053 89.5 L 328.011 62.5 C 328.056 62.243 328.475 62.147 328.5 61.887 L 380.5 61.919 C 380.534 62.194 381.035 62.23 381.094 62.5 L 381.054 70.5 C 381.041 74.59 381.141 78.742 381.5 82.706 C 383.926 77.279 384.793 70.399 389.5 66.201 C 394.811 61.464 401.861 61.846 408.5 61.833 L 423.5 62.079 C 435.874 64.032 443.654 73.02 449.5 83.401 C 451.224 77.781 452.132 71.315 456.5 66.858 C 458.858 64.451 462.219 62.84 465.5 62.189 C 472.426 60.815 494.148 61.256 500.5 64.161 C 506.271 66.8 510.717 71.775 514.769 76.5 L 478.5 112.013 C 471.307 107.587 466.687 108.547 458.5 108.55 L 457.374 109.5 L 457.176 176.5 C 457.235 190.044 458.15 204.159 456.733 217.5 L 430.5 217.713 C 421.894 217.757 412.875 218.203 404.5 217.304 Z M 533.5 217.782 L 481.5 217.764 L 480.889 216.5 L 480.888 182.5 L 481.025 124.5 L 521.5 85.049 C 521.833 85.085 522.167 85.121 522.5 85.157 C 526.87 90.811 530.76 96.529 532.702 103.5 C 535.955 115.176 534.321 128.478 534.381 140.5 L 534.421 216.5 L 533.5 217.782 Z M 558.848 217.5 L 558.304 216.5 L 558.401 198.5 L 558.362 160.5 L 558.5 62.4 C 558.877 62.358 559.127 61.924 559.5 61.856 L 587.5 61.837 L 611.5 61.849 L 612.629 62.5 L 612.834 82.5 L 613.544 82.5 L 615.156 78.5 C 617.082 73.402 618.771 68.325 623.5 65.086 C 631.013 59.939 641.876 61.788 650.5 61.805 C 660.788 61.826 671.39 61.043 681.5 63.218 C 703.292 67.906 719.804 88.148 718.793 110.5 L 668.5 159.972 L 667.302 159.5 L 667.407 132.5 C 667.444 127.473 668.431 121.321 666.77 116.5 C 665.743 113.518 662.479 110.387 659.5 109.362 C 654.252 107.556 647.962 108.566 642.5 108.556 L 613.5 108.573 C 613.433 108.973 612.815 109.108 612.709 109.5 L 612.713 142.5 L 612.5 217.152 L 611.5 217.839 L 586.5 217.733 L 558.848 217.5 Z M 667.902 217.5 C 666.54 203.954 667.133 189.267 667.452 175.5 L 720.5 124.355 L 719.834 125.5 L 719.998 151.5 L 719.725 217.5 L 667.902 217.5 Z M 755.5 197.265 C 742.17 180.68 743.386 162.706 743.355 142.5 C 743.337 130.584 742.532 118.326 744.243 106.5 C 747.703 82.587 768.262 65.362 791.5 62.146 L 829.5 61.81 C 849.605 61.688 871.632 60.035 886.868 75.5 L 848.5 112.994 C 838.898 106.925 831.544 108.494 820.5 108.517 C 814.769 108.528 807.71 107.344 802.5 110.194 C 796.238 113.619 796.484 120.314 796.533 126.5 C 796.618 137.35 797.393 148.812 796.14 159.5 L 757.5 197.111 L 756.5 197.907 L 755.5 197.265 Z M 764.256 205.5 L 802.5 168.151 C 811.143 170.354 820.474 169.722 829.5 169.692 C 833.78 169.678 838.327 170.104 842.5 168.952 C 846.212 167.928 849.799 164.283 850.619 160.5 L 850.902 149.5 L 850.831 126.5 C 850.884 126.13 851.308 125.873 851.339 125.5 L 882.5 95.734 L 894.5 84.036 C 894.793 83.867 895.223 84.054 895.5 83.859 C 907.057 97.415 904.01 115.791 904.02 132.5 L 903.728 169.5 C 901.173 192.6 883.919 212.965 860.5 216.78 C 846.514 219.058 831.633 217.741 817.5 217.723 C 799.712 217.701 777.694 219.422 764.256 205.5 Z"/><path class="wm-b" transform="matrix(0.566168838 0.000000000 0.000000000 0.866443826 -33.666986360 -40.538904408)" d="M 288.5 178.904 L 57.5 178.945 C 57.26 178.693 56.74 178.891 56.5 178.639 L 56.124 177.5 L 56.106 166.5 L 56.5 165.279 C 56.734 165.016 57.266 165.199 57.5 164.936 L 287.5 164.863 C 287.745 165.092 288.255 164.749 288.5 164.978 L 289.252 166.5 L 289.176 177.5 L 288.5 178.904 Z"/></svg></a>
        """;

    private const string SuccessBody = """
          <svg class="icon ok" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M12 2a10 10 0 1 1 0 20 10 10 0 0 1 0-20m3.22 6.97-4.47 4.47-1.97-1.97a.75.75 0 0 0-1.06 1.06l2.5 2.5c.3.3.77.3 1.06 0l5-5a.75.75 0 1 0-1.06-1.06"/></svg>
          <h1>You’re signed in</h1>
          <p class="lede">Return to Temno to keep cutting.<br>You can close this tab.</p>
        """;

    private const string FailureBody = """
          <svg class="icon warn" viewBox="0 0 16 16" aria-hidden="true" focusable="false"><path d="M5.82 2.28a2.5 2.5 0 0 1 4.36 0l4.5 8A2.5 2.5 0 0 1 12.5 14h-9a2.5 2.5 0 0 1-2.18-3.72zM8 9.5A.75.75 0 1 0 8 11a.75.75 0 0 0 0-1.5M8 5a.5.5 0 0 0-.5.5V8a.5.5 0 0 0 1 0V5.5A.5.5 0 0 0 8 5"/></svg>
          <h1>Sign-in didn’t finish</h1>
          <p class="lede">Return to Temno and try again.</p>
        """;

    private const string Tail = """
        <p class="fallback">Temno closed? Open it and sign in again.</p>
        </main>
        <footer>© 2026 Temno</footer>
        </body>
        </html>
        """;
}
