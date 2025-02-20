using UnityEngine;
using UnityEngine.Events;

namespace PolySpatial.Template
{
    public class SpatialUIToggle : SpatialUI
    {
        [SerializeField]
        public UnityEvent<bool> m_ToggleChanged;

        [SerializeField]
        MeshRenderer m_ToggleBackground;

        bool m_Active;

        public bool enableInteraction = true;

        public override void PressStart()
        {
            if (!enableInteraction) return;
            base.PressStart();
            m_PressStart.Invoke();
        }

        public override void PressEnd()
        {
            if (!enableInteraction) return;
            m_PressEnd.Invoke();
            base.PressEnd();
            m_Active = !m_Active;
            m_ToggleChanged.Invoke(m_Active);
            m_ToggleBackground.material.color = m_Active ? m_SelectedColor : m_UnselectedColor;
        }
    }
}
