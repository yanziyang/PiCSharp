using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Exercises LaTeX branches that the upstream renderer suite does not reach.</summary>
public sealed class LatexBranchCoverageTests
{
    [Fact(DisplayName = "renders symbol aliases and escaped special characters")]
    public void Renders_symbol_aliases_and_escaped_special_characters()
    {
        AssertLatex(@"\div+\ast+\star+\circ+\bullet+\cap+\cup+\lbrace x \rbrace+\vert+\Vert+\backslash+\colon", "÷+∗+⋆+∘+•+∩+∪+{ x }+|+‖+\\+:");
        AssertLatex(@"\{\}\$\%\#\_\&\|", "{}$%#_&‖");
    }

    [Fact(DisplayName = "renders positive and negative spacing commands")]
    public void Renders_positive_and_negative_spacing_commands()
    {
        AssertLatex(@"a\,b\:c\;d\>e\enspace f\enskip g\medspace h\quad i\qquad j\thickspace k\thinspace l", "a b c d e f g h i j k l");
        AssertLatex(@"a\!b\negmedspace c\negthickspace d\negthinspace e", "ab c d e");
        AssertLatex(@"a\displaystyle\limits\nolimits\scriptstyle\scriptscriptstyle\textstyle b", "a b");
    }

    [Fact(DisplayName = "falls back to textual scripts and maps partial blackboard alphabets")]
    public void Falls_back_to_textual_scripts_and_maps_partial_blackboard_alphabets()
    {
        AssertLatex(@"x_{foo}+x^{foo}+x_{a+b}+x^{a+b}", "x_foo+xᶠᵒᵒ+x_(a+b)+xᵃ⁺ᵇ");
        AssertLatex(@"\mathbb{CHNRQZAX}", "ℂℍℕℝℚℤAX");
        AssertLatex(@"\sqrt[5]{x}+\sqrt[ab]{x}", "⁵√x+ᵃᵇ√x");
    }

    [Fact(DisplayName = "formats fraction variants according to numerator and denominator complexity")]
    public void Formats_fraction_variants_according_to_numerator_and_denominator_complexity()
    {
        AssertLatex(@"\dfrac{a+b}{x+y}+\tfrac{ab}{xyz}+\frac{a}{xy}+\frac{a}{x}", "(a+b)/(x+y)+ab/(xyz)+a/(xy)+a/x");
        AssertLatex(@"\sqrt{a+b}+\sqrt{xy}+\sqrt{}", "√(a+b)+√xy+√()");
    }

    [Fact(DisplayName = "renders box, binomial, modular, and overlay command variants")]
    public void Renders_box_binomial_modular_and_overlay_command_variants()
    {
        AssertLatex(@"\fbox{x}+\dbinom{n}{k}+\tbinom{n}{k}", "[x]+(n choose k)+(n choose k)");
        AssertLatex(@"a\mod n+b\bmod n+c\pmod m+d\pod r", "a mod n+b mod n+c (mod m)+d (r)");
        AssertLatex(@"\overset{abc}{x}+\underset{abc}{x}+\stackrel{!}{=}", "xᵃᵇᶜ+x_abc+=^!");
    }

    [Fact(DisplayName = "renders named operators with inline limits and operator-name variants")]
    public void Renders_named_operators_with_inline_limits_and_operator_name_variants()
    {
        AssertLatex(@"\lim_{x\to0}x+\operatorname{foo}_{n}+\operatorname*{bar}_{n}", "lim[x→0] x+ foo[n] + bar[n]");
        AssertLatex(@"\operatorname{foo}_{n}", "foo[n]", new RenderLatexOptions { Display = true });
        AssertLatex(@"\operatorname*{bar}_{n}", "bar\n n", new RenderLatexOptions { Display = true });
        Assert.Null(Latex.RenderLatex(@"\sum_{i}^{j}_{k}"));
        Assert.Null(Latex.RenderLatex(@"\sum^{i}^{j}"));
    }

    [Fact(DisplayName = "honors explicit operator limit overrides")]
    public void Honors_explicit_operator_limit_overrides()
    {
        AssertLatex(@"\sum\nolimits_{i=0}^{n}x_i", "∑ᵢ₌₀ⁿxᵢ", new RenderLatexOptions { Display = true });
        AssertLatex(@"\sum\limits_{i=0}^{n}x_i", " n\n ∑  xᵢ\ni=0", new RenderLatexOptions { Display = true });
        AssertLatex(@"\int\limits_0^1 f(x)\,dx", "1\n∫ f(x) dx\n0", new RenderLatexOptions { Display = true });
    }

    [Fact(DisplayName = "renders equation alignment and optional row-break spacing")]
    public void Renders_equation_alignment_and_optional_row_break_spacing()
    {
        AssertLatex(@"\begin{equation}a=b\end{equation}", "a = b");
        AssertLatex(@"\begin{aligned}a&=b\\[4pt]c&=d\end{aligned}", "a = b\nc = d");
        AssertLatex(@"\begin{gather}a\\b\end{gather}", "a\nb");
        AssertLatex(@"\begin{alignedat}{2}a&=b&c&=d\end{alignedat}", "a = b c = d");
    }

    [Fact(DisplayName = "renders matrix environment variants")]
    public void Renders_matrix_environment_variants()
    {
        AssertLatex(@"\begin{array}{cc}a&b\\c&d\end{array}", "a │ b\nc │ d");
        AssertLatex(@"\begin{smallmatrix}a&b\\c&d\end{smallmatrix}", "a │ b\nc │ d");
        AssertLatex(@"\begin{bmatrix}a&b\\c&d\end{bmatrix}", "⎡ a │ b ⎤\n⎣ c │ d ⎦");
        AssertLatex(@"\begin{Bmatrix}a&b\\c&d\end{Bmatrix}", "⎧ a │ b ⎫\n⎩ c │ d ⎭");
        AssertLatex(@"\begin{vmatrix}a&b\\c&d\end{vmatrix}", "│ a │ b │\n│ c │ d │");
        AssertLatex(@"\begin{Vmatrix}a&b\\c&d\end{Vmatrix}", "║ a │ b ║\n║ c │ d ║");
    }

    [Fact(DisplayName = "uses natural condition prefixes in cases environments")]
    public void Uses_natural_condition_prefixes_in_cases_environments()
    {
        AssertLatex(@"\begin{cases}a&when x>0\\b&for x=0\\c&Otherwise\\d&plain\end{cases}", "⎧ a when x > 0\n⎨ b for x = 0\n⎨ c Otherwise\n⎩ d if plain");
        AssertLatex(@"\begin{cases}a\end{cases}", "⎧ a");
    }

    [Fact(DisplayName = "aligns matrix columns using terminal-cell width")]
    public void Aligns_matrix_columns_using_terminal_cell_width()
    {
        AssertLatex(@"\begin{pmatrix}界&x\\a&b\end{pmatrix}", "⎛ 界 │ x ⎞\n⎝ a  │ b ⎠");
        AssertLatex(@"\begin{pmatrix}é&x\\a&b\end{pmatrix}", "⎛ é │ x ⎞\n⎝ a │ b ⎠");
    }

    [Fact(DisplayName = "returns null for malformed optional groups and unsupported environments")]
    public void Returns_null_for_malformed_optional_groups_and_unsupported_environments()
    {
        foreach (var source in new[]
        {
            @"\sqrt[2{x}",
            @"\sqrt[2]{x",
            @"\begin{unknown}x\end{unknown}",
            @"\end{matrix}",
            @"\begin",
            @"\not{}",
            @"\begin{matrix}1\end{array}",
            "a\\",
        })
        {
            Assert.Null(Latex.RenderLatex(source));
        }
    }

    private static void AssertLatex(string source, string expected, RenderLatexOptions? options = null)
    {
        Assert.Equal(expected, Latex.RenderLatex(source, options));
    }
}
