namespace ValleytalkReborn
{
    internal static class BioLabelFitter
    {
        private static readonly float[] ScaleSteps = { 1.0f, 0.9f, 0.78f };
        private const float TextPaddingRatio = 0.92f;
        private const string Ellipsis = "…";
        private static readonly char[] Separators = { ' ', '-', '/', '·', ',', '，', '、', '(', '（', '|', ':' };

        public static (string Label, float Scale) FitBold(string label, float maxWidth, float fontSize)
        {
            if (string.IsNullOrEmpty(label))
                return (string.Empty, 1f);

            float avail = maxWidth * TextPaddingRatio;

            foreach (float s in ScaleSteps)
            {
                if (CustomFontManager.MeasureStringBold(label, fontSize, s).X <= avail)
                    return (label, s);
            }

            float sMin = ScaleSteps[ScaleSteps.Length - 1];

            if (CustomFontManager.MeasureStringBold(label, fontSize, sMin).X <= avail)
                return (label, sMin);

            float ellW = CustomFontManager.MeasureStringBold(Ellipsis, fontSize, sMin).X;

            int n = 0;
            int lo = 0;
            int hi = label.Length;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                float w = CustomFontManager.MeasureStringBold(label.Substring(0, mid), fontSize, sMin).X;
                if (w + ellW <= avail)
                {
                    n = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (n >= label.Length)
                return (label, sMin);

            if (n <= 0)
                return (Ellipsis, sMin);

            int cut = -1;
            for (int i = n - 1; i >= 1; i--)
            {
                char c = label[i];
                foreach (char sep in Separators)
                {
                    if (c == sep)
                    {
                        cut = i;
                        break;
                    }
                }
                if (cut >= 0)
                    break;
            }

            string prefix;
            if (cut >= 0)
                prefix = label.Substring(0, cut).TrimEnd();
            else
                prefix = label.Substring(0, n);

            if (string.IsNullOrEmpty(prefix))
                prefix = label.Substring(0, 1);

            return (prefix + Ellipsis, sMin);
        }
    }
}
