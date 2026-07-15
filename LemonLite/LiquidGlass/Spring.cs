using System;

namespace LemonLite.LiquidGlass
{
    /// <summary>
    /// 质量弹簧阻尼 ODE 积分器，带速度跟踪。
    /// 用于动画化 toggle 的 fraction (0↔1) 与 press progress (0↔1)，呈现临界阻尼般的运动。
    /// 移植自 LiquidGlassDemo.LiquidGlass.Spring，保留原始数值与子步长积分。
    /// </summary>
    public struct Spring
    {
        public float Position;
        public float Velocity;
        public float Stiffness;   // k
        public float Damping;     // c
        public float Mass;        // m
        public float Target;

        private const int Substeps = 4;

        public Spring(float initial, float stiffness = 300f, float damping = 22f, float mass = 1f)
        {
            Position = initial;
            Velocity = 0f;
            Stiffness = stiffness;
            Damping = damping;
            Mass = mass;
            Target = initial;
        }

        /// <summary>半隐式 Euler 积分，带子步长保证稳定性。</summary>
        public void Update(float dt)
        {
            if (dt <= 0f) return;
            float h = dt / Substeps;
            for (int i = 0; i < Substeps; i++)
            {
                float force = -Stiffness * (Position - Target) - Damping * Velocity;
                float accel = force / Math.Max(Mass, 0.0001f);
                Velocity += accel * h;
                Position += Velocity * h;
            }
        }

        public bool IsSettled(float threshold = 0.001f)
        {
            return Math.Abs(Position - Target) < threshold && Math.Abs(Velocity) < threshold;
        }

        /// <summary>为目标响应时间设置临界阻尼系数（启发式）。</summary>
        public static Spring CriticallyDamped(float initial, float responseTime = 0.18f, float mass = 1f)
        {
            // 临界阻尼: c = 2*sqrt(k*m)。响应时间 ~ 1/w 选 k。
            float k = 4f * (float)Math.PI * (float)Math.PI / Math.Max(responseTime * responseTime, 1e-4f);
            float c = 2f * (float)Math.Sqrt(k * mass);
            return new Spring(initial, k, c, mass);
        }
    }
}
