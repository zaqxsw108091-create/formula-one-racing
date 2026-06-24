using System.Collections.Generic;
using UnityEngine;

// 트랙 경로 정의용 헬퍼.
// 사용법: 빈 오브젝트에 이 컴포넌트를 붙이고, 그 '자식'으로 마커(빈 오브젝트)를
//        트랙을 따라 순서대로 놓으면 자식들이 자동으로 웨이포인트가 됩니다.
// - 배열에 일일이 드래그할 필요 없음
// - 씬 뷰에 경로가 그려져(기즈모) 눈으로 보며 조정 가능
// - 컴포넌트 톱니바퀴 메뉴의 "중간점 삽입"으로 촘촘하게 만들 수 있음
public class WaypointPath : MonoBehaviour
{
    public bool loop = true;          // 마지막 → 첫 지점으로 순환(레이스 트랙)
    public float gizmoRadius = 1.0f;  // 씬에 표시되는 마커 크기

    // 자식 Transform들을 순서대로 웨이포인트 배열로 반환
    public Transform[] GetWaypoints()
    {
        var list = new List<Transform>();
        foreach (Transform child in transform)
        {
            list.Add(child);
        }
        return list.ToArray();
    }

    // 인접한 웨이포인트 사이에 중간점을 삽입해 두 배로 촘촘하게 만듦.
    // (컴포넌트 우측 톱니바퀴 ▸ "웨이포인트 촘촘하게" 클릭)
    [ContextMenu("웨이포인트 촘촘하게 (중간점 삽입)")]
    void Densify()
    {
        var pts = new List<Transform>(GetWaypoints());
        if (pts.Count < 2) return;

        int count = pts.Count;
        int limit = loop ? count : count - 1;

        var created = new List<Transform>();
        for (int i = 0; i < limit; i++)
        {
            Vector3 a = pts[i].position;
            Vector3 b = pts[(i + 1) % count].position;
            var mid = new GameObject("WP_mid_" + i).transform;
            mid.position = (a + b) * 0.5f;
            mid.SetParent(transform, true);
            created.Add(mid);
        }

        // 재정렬: 원점 - 중간점 - 원점 - 중간점 ... 순서가 되도록 형제 인덱스 설정
        int si = 0;
        for (int i = 0; i < count; i++)
        {
            pts[i].SetSiblingIndex(si++);
            if (i < created.Count) created[i].SetSiblingIndex(si++);
        }
        Debug.Log("WaypointPath: 웨이포인트 " + count + "개 → " + (count + created.Count) + "개로 촘촘해졌습니다.");
    }

    // 씬 뷰에 경로 시각화
    void OnDrawGizmos()
    {
        Transform[] pts = GetWaypoints();
        if (pts.Length == 0) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < pts.Length; i++)
        {
            Gizmos.DrawSphere(pts[i].position, gizmoRadius);
            int next = i + 1;
            if (next >= pts.Length)
            {
                if (!loop) break;
                next = 0;
            }
            Gizmos.DrawLine(pts[i].position, pts[next].position);
        }

        // 0번(출발/결승선) 강조
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(pts[0].position, gizmoRadius * 1.4f);
    }
}
