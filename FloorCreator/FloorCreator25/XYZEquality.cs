using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FloorCreator
{
    /// <summary>
    ///  Компаратор «почти‑равно» для XYZ.
    ///  Сравнивает точки с заданным допуском (по умолчанию 1E‑6 футов ≈ 0,0003 мм).
    /// </summary>
    public sealed class XYZEquality : IEqualityComparer<XYZ>
    {
        private readonly double _eps;               // допуск в единицах Revit (футы)

        public XYZEquality(double tolerance = 1e-6)
        {
            _eps = tolerance;
        }

        public bool Equals(XYZ a, XYZ b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;

            return
                Math.Abs(a.X - b.X) <= _eps &&
                Math.Abs(a.Y - b.Y) <= _eps &&
                Math.Abs(a.Z - b.Z) <= _eps;
        }

        public int GetHashCode(XYZ p)
        {
            if (p is null) return 0;

            // «Квантуем» координаты по допуску, чтобы одинаковые точки
            // попадали в одну и ту же «ячейку» хэша.
            long qx = (long)Math.Round(p.X / _eps);
            long qy = (long)Math.Round(p.Y / _eps);
            long qz = (long)Math.Round(p.Z / _eps);

            // Классический XOR‑микс: быстрая реализация без коллизий для целых.
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + qx.GetHashCode();
                hash = hash * 31 + qy.GetHashCode();
                hash = hash * 31 + qz.GetHashCode();
                return hash;
            }
        }
    }
}
