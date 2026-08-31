using Xunit;

namespace Pi.Tui.Tests;

/// <summary>Ports the upstream LaTeX renderer cases and preserves their display names.</summary>
public sealed class LatexTests
{
    [Fact(DisplayName = "Jacobian conjecture session using dollar delimiters")]
    public void Jacobian_conjecture_session_using_dollar_delimiters()
    {
        AssertLatex(@"\mathbb{C}^3 \to \mathbb{C}^3", "ℂ³ → ℂ³");
        AssertLatex(@"\{3x+2y,\; 27x^2-4z-1,\; x(x-1)(x+1)\} \quad\Rightarrow\quad x \in \{0, \pm 1\},", "{3x+2y, 27x²-4z-1, x(x-1)(x+1)} ⇒ x ∈ {0, ± 1},");
        AssertLatex(@"F_1 = -\frac{1}{4x^2}.", "F₁ = -1/(4x²).");
        AssertLatex("-2", "-2");
        AssertLatex("(0,0,-1/4)", "(0,0,-1/4)");
        AssertLatex("(1,-3/2,13/2)", "(1,-3/2,13/2)");
        AssertLatex("(1,1,1)", "(1,1,1)");
        AssertLatex("(2,1,0)", "(2,1,0)");
        AssertLatex("(-1/4, 0, 0)", "(-1/4, 0, 0)");
        AssertLatex(@"\{(0,0,-1/4), (1,-3/2,13/2), (-1,3/2,13/2)\}", "{(0,0,-1/4), (1,-3/2,13/2), (-1,3/2,13/2)}");
        AssertLatex("(2,1,1)", "(2,1,1)");
        AssertLatex("(7/3,-2/5,11/7)", "(7/3,-2/5,11/7)");
        AssertLatex(@"\{y - p(x),\; q(x)\}", "{y - p(x), q(x)}");
        AssertLatex(@"\deg q = 3", "deg q = 3");
        AssertLatex(@"[\mathbb{C}(x,y,z):\mathbb{C}(F_1,F_2,F_3)] = 3", "[ℂ(x,y,z):ℂ(F₁,F₂,F₃)] = 3");
        AssertLatex("u = 1+xy", "u = 1+xy");
        AssertLatex("G = u^2 z + y^2(4+3xy)", "G = u² z + y²(4+3xy)");
        AssertLatex("F_1 = uG", "F₁ = uG");
        AssertLatex("F_2 = y + 3xG", "F₂ = y + 3xG");
        AssertLatex("x=0", "x = 0");
        AssertLatex("F_2 = F_3 = 0", "F₂ = F₃ = 0");
        AssertLatex("xy = -3/2", "xy = -3/2");
        AssertLatex("x^2 z = 13/2", "x² z = 13/2");
        AssertLatex(@"\mathbb{C}^*", "ℂ^*");
        AssertLatex(@"s \mapsto (s,\, -\tfrac{3}{2s},\, \tfrac{13}{2s^2})", "s ↦ (s, -3/(2s), 13/(2s²))");
        AssertLatex("X", "X");
        AssertLatex(@"p_\pm", "p_±");
        AssertLatex("F(-x,-y,z) = (F_1, -F_2, -F_3)", "F(-x,-y,z) = (F₁, -F₂, -F₃)");
        AssertLatex("p_0", "p₀");
        AssertLatex(@"s \to \infty", "s → ∞");
        AssertLatex("(0,0,0)", "(0,0,0)");
        AssertLatex(@"\Rightarrow", "⇒");
        AssertLatex(@"\ge 2", "≥ 2");
        AssertLatex(@"\ge 3", "≥ 3");
        AssertLatex("1", "1");
        AssertLatex(@"\mathrm{diag}(-1/2,1,1)", "diag(-1/2,1,1)");
        AssertLatex("4+3xy", "4+3xy");
    }

    [Fact(DisplayName = "satellite calculation session using bracket delimiters")]
    public void Satellite_calculation_session_using_bracket_delimiters()
    {
        AssertLatex(@"E \approx \frac{0.1\ \text{lux}}{100\ \text{lm/W}} = 0.001\ \text{W/m}^2", "E ≈ (0.1 lux)/(100 lm/W) = 0.001 W/m²");
        AssertLatex(@"\boxed{1\ \text{milliwatt per square metre}}", "[1 milliwatt per square metre]");
        AssertLatex(@"5\ \text{km}^2 = 5{,}000{,}000\ \text{m}^2", "5 km² = 5,000,000 m²");
        AssertLatex(@"P_{\text{light}} = 0.001 \times 5{,}000{,}000
= \boxed{5{,}000\ \text{W}}", "P_light = 0.001 × 5,000,000 = [5,000 W]");
        AssertLatex(@"P_{\text{electric}} = 5\ \text{kW} \times 0.2
= \boxed{1\ \text{kW}}", "P_electric = 5 kW × 0.2 = [1 kW]");
        AssertLatex(@"\pi(2.5\ \text{km})^2 = 19.6\ \text{km}^2", "π(2.5 km)² = 19.6 km²");
        AssertLatex(@"0.001\ \text{W/m}^2 \times 19.6 \times 10^6\ \text{m}^2
\approx \boxed{20\ \text{kW optical}}", "0.001 W/m² × 19.6 × 10⁶ m² ≈ [20 kW optical]");
        AssertLatex(@"1\ \text{kW} \times \frac{1}{3600}\ \text{hour}
= \boxed{0.28\ \text{Wh}}", "1 kW × 1/3600 hour = [0.28 Wh]");
    }

    [Fact(DisplayName = "Jacobian conjecture sessions using parenthesis and bracket delimiters")]
    public void Jacobian_conjecture_sessions_using_parenthesis_and_bracket_delimiters()
    {
        AssertLatex(@"\det\!\left(\frac{\partial(F_1,F_2,F_3)}{\partial(x,y,z)}\right)=-2.", "det((∂(F₁,F₂,F₃))/(∂(x,y,z))) = -2.");
        AssertLatex(@"\begin{aligned}
F(0,0,-\tfrac14)&=(-\tfrac14,0,0),\\
F(1,-\tfrac32,\tfrac{13}2)&=(-\tfrac14,0,0),\\
F(-1,\tfrac32,\tfrac{13}2)&=(-\tfrac14,0,0).
\end{aligned}", "F(0,0,-1/4) = (-1/4,0,0),\nF(1,-3/2,13/2) = (-1/4,0,0),\nF(-1,3/2,13/2) = (-1/4,0,0).");
        AssertLatex("F=(F_1,F_2,F_3)", "F = (F₁,F₂,F₃)");
        AssertLatex("F", "F");
        AssertLatex("3", "3");
    }

    [Fact(DisplayName = "Jacobian matrix session using dollar delimiters")]
    public void Jacobian_matrix_session_using_dollar_delimiters()
    {
        AssertLatex(@"J = \begin{pmatrix}
\frac{\partial f_1}{\partial x} & \frac{\partial f_1}{\partial y} & \frac{\partial f_1}{\partial z} \\
\frac{\partial f_2}{\partial x} & \frac{\partial f_2}{\partial y} & \frac{\partial f_2}{\partial z} \\
\frac{\partial f_3}{\partial x} & \frac{\partial f_3}{\partial y} & \frac{\partial f_3}{\partial z}
\end{pmatrix}", "J = ⎛ (∂ f₁)/(∂ x) │ (∂ f₁)/(∂ y) │ (∂ f₁)/(∂ z) ⎞\n    ⎜ (∂ f₂)/(∂ x) │ (∂ f₂)/(∂ y) │ (∂ f₂)/(∂ z) ⎟\n    ⎝ (∂ f₃)/(∂ x) │ (∂ f₃)/(∂ y) │ (∂ f₃)/(∂ z) ⎠");
        AssertLatex(@"\begin{aligned}
f_1 &= (1+xy)^3 z + y^2(1+xy)(4+3xy) \\
f_2 &= y + 3x(1+xy)^2 z + 3xy^2(4+3xy) \\
f_3 &= 2x - 3x^2y - x^3z
\end{aligned}", "f₁ = (1+xy)³ z + y²(1+xy)(4+3xy)\nf₂ = y + 3x(1+xy)² z + 3xy²(4+3xy)\nf₃ = 2x - 3x²y - x³z");
        AssertLatex("x, y, z", "x, y, z");
        AssertLatex("(x, y, z)", "(x, y, z)");
        AssertLatex(@"(0,\; 0,\; -\tfrac14)", "(0, 0, -1/4)");
        AssertLatex(@"(-\tfrac14,\; 0,\; 0)", "(-1/4, 0, 0)");
        AssertLatex(@"(1,\; -\tfrac32,\; \tfrac{13}{2})", "(1, -3/2, 13/2)");
        AssertLatex(@"(-1,\; \tfrac32,\; \tfrac{13}{2})", "(-1, 3/2, 13/2)");
        AssertLatex(@"(-\frac14, 0, 0)", "(-1/4, 0, 0)");
        AssertLatex(@"F: \mathbb{C}^3 \to \mathbb{C}^3", "F: ℂ³ → ℂ³");
        AssertLatex(@"F(0,0,-\tfrac14) = F(1,-\tfrac32,\tfrac{13}{2}) = F(-1,\tfrac32,\tfrac{13}{2}) = (-\tfrac14, 0, 0)", "F(0,0,-1/4) = F(1,-3/2,13/2) = F(-1,3/2,13/2) = (-1/4, 0, 0)");
        AssertLatex(@"\mathbb{C}^3", "ℂ³");
        AssertLatex(@"\begin{aligned}
f_1 &= \frac{f_1^{\text{ut}}(u,t)}{x^2}, \quad
f_2 = \frac{f_2^{\text{ut}}(u,t)}{x}, \quad
f_3 = x\,(2 - 3u - t)
\end{aligned}", "f₁ = (f₁ᵘᵗ(u,t))/(x²), f₂ = (f₂ᵘᵗ(u,t))/x, f₃ = x (2 - 3u - t)");
        AssertLatex(@"\det J_F", "det J_F");
        AssertLatex(@"(-\tfrac14, 0, 0)", "(-1/4, 0, 0)");
        AssertLatex("u = xy", "u = xy");
        AssertLatex("t = x^2z", "t = x²z");
        AssertLatex(@"x \neq 0", "x ≠ 0");
        AssertLatex(@"f_1^{\text{ut}}, f_2^{\text{ut}}", "f₁ᵘᵗ, f₂ᵘᵗ");
        AssertLatex("u,t", "u,t");
        AssertLatex("x", "x");
        AssertLatex("x, x^2", "x, x²");
        AssertLatex(@"\mathbb{C}^n \to \mathbb{C}^n", "ℂⁿ → ℂⁿ");
        AssertLatex(@"n \geq 2", "n ≥ 2");
        AssertLatex(@"\mathbb{P}^3", "ℙ³");
    }

    [Fact(DisplayName = "extended formulas from a renderer stress-test session")]
    public void Extended_formulas_from_a_renderer_stress_test_session()
    {
        AssertLatex(@"e^{i\pi}+1=0", "e^(iπ)+1 = 0");
        AssertLatex(@"\boxed{
\mathcal{Z}(\beta)
=
\int_{\mathcal M}
\exp\!\left(
-\beta\left[
\frac12 g^{ij}(x)\,\partial_i\phi\,\partial_j\phi
+V(\phi)
\right]\right)
\mathcal D\phi
}", "[Z(β) = ∫_M exp( -β[ 1/2 gⁱʲ(x) ∂ᵢϕ ∂ⱼϕ +V(ϕ) ]) Dϕ]");
        AssertLatex(@"\begin{aligned}
\nabla_\mu T^{\mu\nu}
&=
\frac{1}{\sqrt{-g}}
\partial_\mu\!\left(\sqrt{-g}\,T^{\mu\nu}\right)
+\Gamma^\nu_{\mu\lambda}T^{\mu\lambda}
=0, \\
R_{\mu\nu}-\frac12 Rg_{\mu\nu}+\Lambda g_{\mu\nu}
&=
\frac{8\pi G}{c^4}T_{\mu\nu}.
\end{aligned}", "∇_μ T^(μν) = 1/(√(-g)) ∂_μ(√(-g) T^(μν)) +Γ^ν_(μλ)T^(μλ) = 0,\nR_(μν)-1/2 Rg_(μν)+Λ g_(μν) = (8π G)/(c⁴)T_(μν).");
        AssertLatex(@"f(z)
=
\frac{1}{2\pi i}
\oint_{\gamma}
\frac{f(\zeta)}{\zeta-z}\,d\zeta,
\qquad
\det\!\begin{pmatrix}
\lambda-a & -b & 0\\
-c & \lambda-d & -e\\
0 & -f & \lambda-g
\end{pmatrix}
=0.", "f(z) = 1/(2π i) ∮_γ (f(ζ))/(ζ-z) dζ, det⎛ λ-a │ -b  │ 0   ⎞ = 0.\n                                        ⎜ -c  │ λ-d │ -e  ⎟\n                                        ⎝ 0   │ -f  │ λ-g ⎠");
        AssertLatex(@"\Psi(x,t)=
\sum_{n=1}^{\infty}
\underbrace{
c_n
\sqrt{\frac{2}{L}}
\sin\!\left(\frac{n\pi x}{L}\right)
}_{\text{spatial eigenmode}}
\exp\!\left(-\frac{i\hbar n^2\pi^2}{2mL^2}t\right),
\qquad
|\Psi(x,t)|^2
=
\begin{cases}
\Psi^\ast\Psi, & 0<x<L,\\
0, & \text{otherwise}.
\end{cases}", "Ψ(x,t) = ∑ₙ₌₁^∞ cₙ √(2/L) sin((nπ x)/L)_(spatial eigenmode) exp(-(iℏ n²π²)/(2mL²)t), |Ψ(x,t)|² = ⎧ Ψ^∗Ψ if 0 < x < L,\n⎩ 0 otherwise.");
        AssertLatex(@"x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}", "x = (-b±√(b²-4ac))/(2a)");
        AssertLatex(@"\int_0^\infty e^{-x^2}\,dx=\frac{\sqrt{\pi}}{2}", "∫₀^∞ e^(-x²) dx = (√π)/2");
        AssertLatex(@"e^{i\theta}=\cos\theta+i\sin\theta", "e^(iθ) = cos θ+i sin θ");
        AssertLatex(@"\sum_{n=1}^{\infty}\frac{1}{n^2}=\frac{\pi^2}{6}", "∑ₙ₌₁^∞1/(n²) = π²/6");
        AssertLatex(@"\lim_{x\to 0}\frac{\sin x}{x}=1", "lim[x→0] (sin x)/x = 1");
        AssertLatex(@"\lim_{n\to\infty}
\left(1+\frac{1}{n}\right)^n=e", "lim[n→∞] (1+1/n)ⁿ = e");
        AssertLatex(@"\int_0^1 \frac{x^2}{1+x^3}\,dx
=\frac{1}{3}\ln 2", "∫₀¹ x²/(1+x³) dx = 1/3 ln 2");
        AssertLatex(@"\sum_{k=1}^{n}\frac{k}{k+1}
=n+1-H_{n+1}", "∑ₖ₌₁ⁿk/(k+1) = n+1-Hₙ₊₁");
        AssertLatex(@"\frac{
  \displaystyle \frac{x^2+1}{x-1}
  -
  \displaystyle \frac{2x}{x+1}
}{
  \displaystyle \frac{x}{x^2-1}
}", "((x²+1)/(x-1) - 2x/(x+1))/(x/(x²-1))");
        AssertLatex(@"\lim_{x\to 0}
\frac{
  \displaystyle \frac{\sin x}{x}-1
}{
  \displaystyle \frac{e^x-1}{x}-1
}
=0", "lim[x→0] ((sin x)/x-1)/((eˣ-1)/x-1) = 0");
        AssertLatex(@"\frac{
  1+\displaystyle\frac{1}{1+\frac{1}{x}}
}{
  1-\displaystyle\frac{1}{1-\frac{1}{x}}
}", "(1+1/(1+1/x))/(1-1/(1-1/x))");
        AssertLatex(@"\sum_{n=1}^{\infty}
\frac{
  \displaystyle \frac{1}{n}-\frac{1}{n+1}
}{
  \displaystyle 1+\frac{1}{n^2}
}", "∑ₙ₌₁^∞ (1/n-1/(n+1))/(1+1/(n²))");
    }

    [Fact(DisplayName = "renders common symbols, roots, sums, and integrals")]
    public void Renders_common_symbols_roots_sums_and_integrals()
    {
        AssertLatex(@"\sum_{i=0}^n \alpha_i + \int_0^\infty e^{-x^2}\,dx = \sqrt{\pi}", "∑ᵢ₌₀ⁿ αᵢ + ∫₀^∞ e^(-x²) dx = √π");
    }

    [Fact(DisplayName = "renders common accents and binomial notation")]
    public void Renders_common_accents_and_binomial_notation()
    {
        AssertLatex(@"\binom{n}{k}+\vec{x}+\hat{y}+\overline{AB}", "(n choose k)+x⃗+ŷ+overline(AB)");
    }

    [Fact(DisplayName = "renders extended symbols and negated relations")]
    public void Renders_extended_symbols_and_negated_relations()
    {
        AssertLatex(@"\epsilon+\varepsilon+\varsigma+\varkappa+\oplus+\otimes+\therefore+\because", "ϵ+ε+ς+ϰ+⊕+⊗+∴+∵");
        AssertLatex(@"A\not\subseteq B,\quad x\not\in X", "A ⊈ B, x ∉ X");
    }

    [Fact(DisplayName = "renders delimiter commands and invisible delimiters")]
    public void Renders_delimiter_commands_and_invisible_delimiters()
    {
        AssertLatex(@"\lvert{x}\rvert+\lVert{v}\rVert+\left.\frac{dy}{dx}\right|_{x=0}", "|x|+‖v‖+dy/(dx)|ₓ₌₀");
        AssertLatex(@"\left\lbrace x \middle| x>0 \right\rbrace", "{ x | x > 0 }");
    }

    [Fact(DisplayName = "renders named, modular, overlaid, and underlaid operators")]
    public void Renders_named_modular_overlaid_and_underlaid_operators()
    {
        AssertLatex(@"\operatorname*{arg\,max}_{x\in X} f(x)", "arg max[x∈X] f(x)");
        AssertLatex(@"a\bmod n,\quad a\equiv b\pmod n", "a mod n, a ≡ b (mod n)");
        AssertLatex(@"\overset{!}{=}+\underset{n}{x}+\stackrel{def}{=}", "=^!+xₙ+=ᵈᵉᶠ");
    }

    [Fact(DisplayName = "renders indexed roots and additional accents and wrappers")]
    public void Renders_indexed_roots_and_additional_accents_and_wrappers()
    {
        AssertLatex(@"\sqrt[2]{x}+\sqrt[3]{x}+\sqrt[4]{x}+\sqrt[n]{x}+\sqrt[k]{x+1}", "√x+∛x+∜x+ⁿ√x+ᵏ√(x+1)");
        AssertLatex(@"\acute{x}+\grave{y}+\widehat{xyz}+\overrightarrow{AB}", "x́+ỳ+widehat(xyz)+overrightarrow(AB)");
        AssertLatex(@"\textnormal{hello}+\mbox{world}+\boldsymbol{x}", "hello+world+x");
    }

    [Fact(DisplayName = "renders additional display environments")]
    public void Renders_additional_display_environments()
    {
        AssertLatex(@"\begin{equation}\begin{split}a&=b\\&=c\end{split}\end{equation}", "a = b\n= c");
        AssertLatex(@"\begin{alignedat}{2}a&=b&\quad c&=d\\e&=f&g&=h\end{alignedat}", "a = b c = d\ne = f g = h");
    }

    [Fact(DisplayName = "uses natural case conditions and aligns matrix columns")]
    public void Uses_natural_case_conditions_and_aligns_matrix_columns()
    {
        AssertLatex(@"\begin{cases}a & x<0 \\ b & \text{if }x=0 \\ c & \text{otherwise}\end{cases}", "⎧ a if x < 0\n⎨ b if x = 0\n⎩ c otherwise");
        AssertLatex(@"\begin{pmatrix}1&200\\3000&4\end{pmatrix}", "⎛ 1    │ 200 ⎞\n⎝ 3000 │ 4   ⎠");
    }

    [Fact(DisplayName = "composes matrices with fractions and adjacent matrices")]
    public void Composes_matrices_with_fractions_and_adjacent_matrices()
    {
        AssertLatex(@"R\left(\frac{\pi}{4}\right)
=
\begin{pmatrix}
\frac{\sqrt{2}}{2} & -\frac{\sqrt{2}}{2}\\
\frac{\sqrt{2}}{2} & \frac{\sqrt{2}}{2}
\end{pmatrix}.", "   π\nR( ─ ) = ⎛ (√2)/2 │ -(√2)/2 ⎞\n   4     ⎝ (√2)/2 │ (√2)/2  ⎠.", new RenderLatexOptions { Display = true });
        AssertLatex(@"\mathbf w
=
R\left(\frac{\pi}{4}\right)
\begin{pmatrix}1\\0\end{pmatrix}
=
\begin{pmatrix}\frac{\sqrt{2}}{2}\\\frac{\sqrt{2}}{2}\end{pmatrix}.", "       π\nw = R( ─ ) ⎛ 1 ⎞ = ⎛ (√2)/2 ⎞\n       4   ⎝ 0 ⎠   ⎝ (√2)/2 ⎠.", new RenderLatexOptions { Display = true });
        AssertLatex(@"A\mathbf e_1=\begin{pmatrix}\pi\\0\end{pmatrix},\qquad A\mathbf e_2=\begin{pmatrix}0\\\frac{1}{\pi}\end{pmatrix}.", "Ae₁ = ⎛ π ⎞, Ae₂ = ⎛ 0   ⎞\n      ⎝ 0 ⎠        ⎝ 1/π ⎠.", new RenderLatexOptions { Display = true });
        AssertLatex(@"\sum_{i=0}^n x_i=\begin{pmatrix}a&b\\c&d\end{pmatrix}.", " n\n ∑  xᵢ = ⎛ a │ b ⎞\ni=0      ⎝ c │ d ⎠.", new RenderLatexOptions { Display = true });
    }

    [Fact(DisplayName = "normalizes relation, multiplication, and named-operator spacing")]
    public void Normalizes_relation_multiplication_and_named_operator_spacing()
    {
        foreach (var source in new[] { "x=y", "x =y", "x=\ny", "x\n=\ny" })
        {
            AssertLatex(source, "x = y");
        }

        AssertLatex("x_{i=0}", "xᵢ₌₀");
        AssertLatex(@"x\neq0", "x ≠ 0");
        AssertLatex(@"A\to B", "A → B");
        AssertLatex(@"\pi\cdot\frac{1}{\pi}", "π · 1/π");
        AssertLatex(@"\sin\theta", "sin θ");
        AssertLatex(@"\sin^2 x", "sin² x");
        AssertLatex(@"-\sin\theta", "-sin θ");
        AssertLatex(@"i\sin\theta", "i sin θ");
        AssertLatex(@"\det(A)", "det(A)");
    }

    [Fact(DisplayName = "treats a backslash followed by a line ending as control space")]
    public void Treats_a_backslash_followed_by_a_line_ending_as_control_space()
    {
        AssertLatex(@"\boxed{
(1,1,1),\ (1,1,2),\ (1,2,5),\ (1,5,13),\ (2,5,29),\
(1,13,34),\ (1,34,89)
}.", "[(1,1,1), (1,1,2), (1,2,5), (1,5,13), (2,5,29), (1,13,34), (1,34,89)].", new RenderLatexOptions { Display = true });
        AssertLatex("a\\" + "\r\nb", "a b");
    }

    [Fact(DisplayName = "stacks operator limits in display mode")]
    public void Stacks_operator_limits_in_display_mode()
    {
        AssertLatex(@"\sum_{i=0}^n x_i", " n\n ∑  xᵢ\ni=0", new RenderLatexOptions { Display = true });
        AssertLatex(@"\min_{x\in X} f(x)", "min f(x)\nx∈X", new RenderLatexOptions { Display = true });
        AssertLatex(@"\operatorname*{arg\,max}_{x\in X} f(x)", "arg max f(x)\n  x∈X", new RenderLatexOptions { Display = true });
        AssertLatex(@"\int\nolimits_0^1 f(x)\,dx", "∫₀¹ f(x) dx", new RenderLatexOptions { Display = true });
        AssertLatex(@"\int\limits_0^1 f(x)\,dx", "1\n∫ f(x) dx\n0", new RenderLatexOptions { Display = true });
    }

    [Fact(DisplayName = "uses the middle brace for intermediate case rows")]
    public void Uses_the_middle_brace_for_intermediate_case_rows()
    {
        AssertLatex(@"\begin{cases}a & x<0 \\ b & x=0 \\ c & x>0\end{cases}", "⎧ a if x < 0\n⎨ b if x = 0\n⎩ c if x > 0");
    }

    [Fact(DisplayName = "stacks fractions in display mode")]
    public void Stacks_fractions_in_display_mode()
    {
        AssertLatex(@"x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}", "    -b±√(b²-4ac)\nx = ────────────\n         2a", new RenderLatexOptions { Display = true });
        AssertLatex(@"\frac{x^2+1}{x-1}", "x²+1\n────\nx-1", new RenderLatexOptions { Display = true });
        AssertLatex("\\frac{1}\n{2}", "1\n─\n2", new RenderLatexOptions { Display = true });
    }

    [Fact(DisplayName = "keeps nested display fractions linear")]
    public void Keeps_nested_display_fractions_linear()
    {
        AssertLatex(@"\frac{\frac{x^2+1}{x-1}-\frac{2x}{x+1}}{\frac{x}{x^2-1}}", "(x²+1)/(x-1)-2x/(x+1)\n─────────────────────\n      x/(x²-1)", new RenderLatexOptions { Display = true });
        AssertLatex(@"\lim_{x\to 0}\frac{\frac{\sin x}{x}-1}{\frac{e^x-1}{x}-1}=0", "     (sin x)/x-1\nlim  ─────────── = 0\nx→0  (eˣ-1)/x-1", new RenderLatexOptions { Display = true });
        AssertLatex(@"\frac{1+\frac{1}{1+\frac{1}{x}}}{1-\frac{1}{1-\frac{1}{x}}}", "1+1/(1+1/x)\n───────────\n1-1/(1-1/x)", new RenderLatexOptions { Display = true });
    }

    [Fact(DisplayName = "keeps fractions linear in scripts and text-style fractions")]
    public void Keeps_fractions_linear_in_scripts_and_text_style_fractions()
    {
        AssertLatex(@"e^{\frac{1}{2}}", "e^(1/2)", new RenderLatexOptions { Display = true });
        AssertLatex(@"\tfrac{1}{2}", "1/2", new RenderLatexOptions { Display = true });
    }

    [Fact(DisplayName = "returns undefined for unsupported commands")]
    public void Returns_undefined_for_unsupported_commands()
    {
        Assert.Null(Latex.RenderLatex(@"x + \unknown{y}"));
    }

    [Fact(DisplayName = "returns undefined for malformed groups and environments")]
    public void Returns_undefined_for_malformed_groups_and_environments()
    {
        foreach (var source in new[] { @"\frac{1}{x", "x}", @"\begin{matrix}1 & 2", "x\\" })
        {
            Assert.Null(Latex.RenderLatex(source));
        }
    }

    private static void AssertLatex(string source, string expected, RenderLatexOptions? options = null)
    {
        Assert.Equal(expected, Latex.RenderLatex(source, options));
    }
}
