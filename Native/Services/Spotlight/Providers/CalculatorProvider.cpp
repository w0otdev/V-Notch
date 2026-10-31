#include "vnotch_common.h"
#include <cstdlib>

using namespace vnotch;

namespace vnotch::services
{
    class CalculatorProvider final
    {
    public:
            static void skip_ws(std::u16string_view s, size_t& pos) noexcept
            {
                while (pos < s.size() && vnotch::is_whitespace(s[pos]))
                    ++pos;
            }

            static bool try_parse_decimal_double(std::u16string_view s, double& out_val) noexcept
            {
                if (s.empty() || s.size() > 64) return false;
                int dots = 0;
                int digits = 0;
                char buf[72]{};
                for (size_t i = 0; i < s.size(); ++i)
                {
                    char16_t c = s[i];
                    if (vnotch::is_ascii_digit(c))
                    {
                        ++digits;
                        buf[i] = static_cast<char>(c);
                    }
                    else if (c == u'.')
                    {
                        if (++dots > 1) return false;
                        buf[i] = '.';
                    }
                    else
                    {
                        return false;
                    }
                }
                if (digits == 0) return false;
                char* end_ptr = nullptr;
                out_val = std::strtod(buf, &end_ptr);
                return end_ptr == buf + s.size() && std::isfinite(out_val);
            }
        
            static bool try_parse_factor(std::u16string_view s, size_t& pos, double& value, int depth) noexcept
            {
                if (depth > 64) return false;
                skip_ws(s, pos);
                value = 0.0;
                if (pos >= s.size()) return false;
        
                if (s[pos] == u'+' || s[pos] == u'-')
                {
                    char16_t sign = s[pos++];
                    double inner = 0.0;
                    if (!try_parse_factor(s, pos, inner, depth + 1)) return false;
                    value = (sign == u'-') ? -inner : inner;
                    return std::isfinite(value);
                }
        
                if (s[pos] == u'(')
                {
                    ++pos;
                    if (!try_parse_add_sub(s, pos, value, depth + 1) || !std::isfinite(value)) return false;
                    skip_ws(s, pos);
                    if (pos >= s.size() || s[pos] != u')') return false;
                    ++pos;
                    return true;
                }
        
                size_t start = pos;
                while (pos < s.size() && (vnotch::is_ascii_digit(s[pos]) || s[pos] == u'.'))
                    ++pos;
        
                return pos > start && try_parse_decimal_double(s.substr(start, pos - start), value) && std::isfinite(value);
            }
        
            static bool try_parse_mul_div_mod(std::u16string_view s, size_t& pos, double& value, int depth) noexcept
            {
                if (!try_parse_factor(s, pos, value, depth) || !std::isfinite(value)) return false;
                while (true)
                {
                    skip_ws(s, pos);
                    if (pos >= s.size() || (s[pos] != u'*' && s[pos] != u'/' && s[pos] != u'%'))
                        return true;
                    char16_t op = s[pos++];
                    double rhs = 0.0;
                    if (!try_parse_factor(s, pos, rhs, depth) || !std::isfinite(rhs)) return false;
                    if (op == u'*')
                    {
                        value *= rhs;
                    }
                    else if (op == u'/')
                    {
                        if (rhs == 0.0) return false;
                        value /= rhs;
                    }
                    else
                    {
                        if (rhs == 0.0) return false;
                        value = std::fmod(value, rhs);
                    }
                    if (!std::isfinite(value)) return false;
                }
            }
        
            static bool try_parse_add_sub(std::u16string_view s, size_t& pos, double& value, int depth) noexcept
            {
                if (!try_parse_mul_div_mod(s, pos, value, depth) || !std::isfinite(value)) return false;
                while (true)
                {
                    skip_ws(s, pos);
                    if (pos >= s.size() || (s[pos] != u'+' && s[pos] != u'-'))
                        return true;
                    char16_t op = s[pos++];
                    double rhs = 0.0;
                    if (!try_parse_mul_div_mod(s, pos, rhs, depth) || !std::isfinite(rhs)) return false;
                    value = (op == u'+') ? (value + rhs) : (value - rhs);
                    if (!std::isfinite(value)) return false;
                }
            }

        static int32_t vn_calculator_try_evaluate_impl(const char16_t* query_ptr, int32_t query_len,
    double* out_value,
    uint8_t* out_valid) noexcept
        {
    if (!out_value || !out_valid) return VN_ERR_INVALID_ARG;
    *out_value = 0.0;
    *out_valid = 0;

    std::u16string_view expr = vnotch::trim(vnotch::make_view(query_ptr, query_len));
    if (expr.empty() || expr.size() > 64) return VN_OK;

    bool has_ascii_digit = false;
    bool has_op = false;
    for (size_t i = 0; i < expr.size(); ++i)
    {
        char16_t c = expr[i];
        if (vnotch::is_ascii_digit(c))
        {
            has_ascii_digit = true;
        }
        else if (vnotch::is_whitespace(c) || c == u'(' || c == u')' || c == u'.')
        {
            // valid
        }
        else if (c == u'+' || c == u'*' || c == u'/' || c == u'%')
        {
            has_op = true;
        }
        else if (c == u'-')
        {
            if (i >= 1) has_op = true;
        }
        else
        {
            return VN_OK;
        }
    }

    if (!has_ascii_digit || !has_op) return VN_OK;

    size_t pos = 0;
    double val = 0.0;
    if (!try_parse_add_sub(expr, pos, val, 0)) return VN_OK;
    skip_ws(expr, pos);
    if (pos != expr.size() || std::isnan(val) || std::isinf(val)) return VN_OK;

    *out_value = val;
    *out_valid = 1;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_calculator_try_evaluate(const char16_t* query_ptr, int32_t query_len,
    double* out_value,
    uint8_t* out_valid) noexcept
{
    return vnotch::services::CalculatorProvider::vn_calculator_try_evaluate_impl(query_ptr, query_len, out_value, out_valid);
}

